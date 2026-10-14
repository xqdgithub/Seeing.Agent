using System.Collections.Concurrent;

namespace Seeing.Agent.Files.Watching;

/// <summary>按根路径池化 RootWatcher + 引用计数。</summary>
public sealed class FileWatcherPool : IDisposable
{
    private sealed class Entry
    {
        public required RootWatcher Watcher;
        public int RefCount;
    }

    private readonly ConcurrentDictionary<string, Entry> _roots = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>当前池化根数量（测试用）。</summary>
    internal int RootCount => _roots.Count;

    /// <summary>按根订阅；调用方 Dispose 时 Release。</summary>
    internal RootWatcher.Subscription Subscribe(string root)
    {
        var normalized = Normalize(root);
        var entry = _roots.GetOrAdd(normalized, _ => new Entry { Watcher = new RootWatcher(normalized) });
        Interlocked.Increment(ref entry.RefCount);
        return new RootWatcher.Subscription(entry.Watcher.Register(), () => ReleaseRoot(normalized));
    }

    /// <summary>释放某根的一个引用；归零则 dispose watcher。</summary>
    public void ReleaseRoot(string root)
    {
        var normalized = Normalize(root);
        if (!_roots.TryGetValue(normalized, out var entry)) return;
        if (Interlocked.Decrement(ref entry.RefCount) <= 0 && _roots.TryRemove(normalized, out var removed))
            removed.Watcher.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (_, e) in _roots) e.Watcher.Dispose();
        _roots.Clear();
    }

    private static string Normalize(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
