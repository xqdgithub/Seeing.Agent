using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Files.Watching;
using Seeing.Agent.Paths;
using Seeing.Session.Core;
using System.Collections.Concurrent;

namespace Seeing.Agent.Files;

/// <summary>
/// 按会话解析并缓存多根文件工作区 facade。
/// <para>
/// 根集合的唯一来源是 <see cref="ISessionRootSet.Roots"/>（项目根 + 协同目录）；
/// 本类不再自己推导"哪些根"。缓存无引用计数：<see cref="Release"/> 幂等全清，
/// watcher 生命周期由 <see cref="FileWatcherPool"/> 的根级引用负责。
/// </para>
/// </summary>
public sealed class FileWorkspaceFactory : IFileWorkspaceFactory, IDisposable
{
    private readonly ISessionRootSet _roots;
    private readonly ISessionManager _sessions;
    private readonly IFileSystem _fileSystem;
    private readonly IWorkspacePathGate _gate;
    private readonly FileWatcherPool _watchers;
    private readonly IPathWriteLock _writeLock;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly object _sync = new();

    private sealed record Entry(Dictionary<string, IFileWorkspace> Workspaces);

    /// <summary>注入依赖。</summary>
    public FileWorkspaceFactory(
        ISessionRootSet roots,
        ISessionManager sessions,
        IFileSystem fileSystem,
        IWorkspacePathGate gate,
        FileWatcherPool watchers,
        IPathWriteLock writeLock)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _watchers = watchers ?? throw new ArgumentNullException(nameof(watchers));
        _writeLock = writeLock ?? throw new ArgumentNullException(nameof(writeLock));
    }

    /// <inheritdoc />
    public async Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default)
    {
        var all = await ListAsync(sessionId, ct).ConfigureAwait(false);

        // 主根 = 项目根（本期会话 cwd 恒等于它）；没有项目根时退回第一个根。
        var primary = all.FirstOrDefault(w => w.Root.Kind == FileWorkspaceRootKind.Project)
            ?? all.FirstOrDefault();

        if (primary is null)
            throw new InvalidOperationException($"会话 {sessionId} 没有可用的文件根（工作区未决议？）");

        return primary;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IFileWorkspace>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 根集合是同步只读内存投影：冷会话必须先预热，否则协同目录会被当成"没有"
        // （典型场景：进程重启后直接打开旧会话）。
        await _sessions.TryGetOrLoadAsync(sessionId, ct).ConfigureAwait(false);

        var declared = _roots.Roots(sessionId);
        if (declared.Count == 0)
            return Array.Empty<IFileWorkspace>();

        lock (_sync)
        {
            var entry = _cache.TryGetValue(sessionId, out var existing)
                ? existing
                : new Entry(new Dictionary<string, IFileWorkspace>());

            // 增量对齐：新增创建、移除回收
            var live = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<IFileWorkspace>(declared.Count);

            foreach (var root in declared)
            {
                var rootId = ComputeRootId(root.Path);
                live.Add(rootId);

                var kind = root.Kind == SessionRootKind.Collaboration
                    ? FileWorkspaceRootKind.Collaboration
                    : FileWorkspaceRootKind.Project;

                if (!entry.Workspaces.TryGetValue(rootId, out var workspace))
                {
                    workspace = new FileWorkspace(
                        sessionId,
                        root.Path,
                        _fileSystem,
                        _gate,
                        _watchers,
                        _writeLock,
                        kind,
                        root.Label);
                    entry.Workspaces[rootId] = workspace;
                }
                else if (workspace is FileWorkspace concrete)
                {
                    // 缓存命中也要刷新投影事实：目录被删除/恢复后 UI 的「不可用」标记依赖它
                    concrete.SyncRoot(kind, root.Label, root.Exists);
                }

                ordered.Add(workspace);
            }

            foreach (var stale in entry.Workspaces.Keys.Where(k => !live.Contains(k)).ToArray())
                entry.Workspaces.Remove(stale);

            _cache[sessionId] = entry;
            return ordered;
        }
    }

    /// <inheritdoc />
    public Task<IFileWorkspace?> GetAsync(string sessionId, string rootId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            return Task.FromResult(
                _cache.TryGetValue(sessionId, out var entry)
                && entry.Workspaces.TryGetValue(rootId, out var workspace)
                    ? workspace
                    : null);
        }
    }

    /// <inheritdoc />
    public async Task<IFileWorkspace?> ResolveAsync(
        string sessionId, string absolutePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
            return null;

        var all = await ListAsync(sessionId, ct).ConfigureAwait(false);

        // 最长前缀优先：避免 D:\a 命中 D:\ab
        return all
            .Where(w => PathBoundary.IsWithin(absolutePath, w.RootPath))
            .OrderByDescending(w => w.RootPath.Length)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public void Release(string sessionId)
    {
        // 幂等全清；不做引用计数（读操作不应带副作用）。
        lock (_sync)
        {
            _cache.TryRemove(sessionId, out _);
        }
    }

    /// <summary>工厂自身不持有池的所有权（池由 DI 释放），显式空实现避免误释放共享实例。</summary>
    public void Dispose()
    {
    }

    private static string ComputeRootId(string root)
    {
        var normalized = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
        if (OperatingSystem.IsWindows()) normalized = normalized.ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
    }
}
