using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Files.Watching;

namespace Seeing.Agent.Files;

/// <summary>按 (sessionId, 有效根) 解析并缓存文件工作区 facade。</summary>
public sealed class FileWorkspaceFactory : IFileWorkspaceFactory, IDisposable
{
    private readonly ISessionWorkspaceResolver _sessions;
    private readonly IWorkspaceProvider _workspace;
    private readonly IFileSystem _fileSystem;
    private readonly IWorkspacePathGate _gate;
    private readonly FileWatcherPool _watchers;
    private readonly IPathWriteLock _writeLock;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly object _sync = new();

    private sealed record Entry(IFileWorkspace Workspace, string Root, int RefCount);

    /// <summary>注入依赖。</summary>
    public FileWorkspaceFactory(
        ISessionWorkspaceResolver sessions,
        IWorkspaceProvider workspace,
        IFileSystem fileSystem,
        IWorkspacePathGate gate,
        FileWatcherPool watchers,
        IPathWriteLock writeLock)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _watchers = watchers ?? throw new ArgumentNullException(nameof(watchers));
        _writeLock = writeLock ?? throw new ArgumentNullException(nameof(writeLock));
    }

    /// <inheritdoc />
    public Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default)
    {
        var cwd = _sessions.ResolveWorkingDirectory(sessionId);
        var root = string.IsNullOrEmpty(cwd) ? _workspace.GetProjectRoot() : cwd;
        root = Path.GetFullPath(root);

        lock (_sync)
        {
            if (_cache.TryGetValue(sessionId, out var existing) && existing.Root == root)
            {
                _cache[sessionId] = existing with { RefCount = existing.RefCount + 1 };
                return Task.FromResult(existing.Workspace);
            }

            // 有效根变化：直接以新根替换（旧 facade 无 OS 资源，watcher 由订阅自身引用计数托管）
            var workspace = new FileWorkspace(sessionId, root, _fileSystem, _gate, _watchers, _writeLock);
            _cache[sessionId] = new Entry(workspace, root, 1);
            return Task.FromResult<IFileWorkspace>(workspace);
        }
    }

    /// <inheritdoc />
    public void Release(string sessionId)
    {
        // 引用计数读-改-写需串行化，避免并发 circuit 下丢失递减
        lock (_sync)
        {
            if (!_cache.TryGetValue(sessionId, out var entry))
                return;
            var next = entry with { RefCount = entry.RefCount - 1 };
            if (next.RefCount <= 0)
                _cache.TryRemove(sessionId, out _);
            else
                _cache[sessionId] = next;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _watchers.Dispose();
}
