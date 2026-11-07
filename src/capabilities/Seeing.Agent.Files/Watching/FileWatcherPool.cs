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
    private int _disposedFlag;

    /// <summary>当前池化根数量（测试用）。</summary>
    internal int RootCount => _roots.Count;

    /// <summary>按根订阅；调用方 Dispose 时 Release。</summary>
    internal RootWatcher.Subscription Subscribe(string root)
    {
        // 显式失败优于静默降级：池已真正 Dispose 后返回「订阅成功但永不触发」的
        // watcher，会让文件变更静默不再刷新（模块停用再启用后的历史症状）。
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);

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

    /// <summary>
    /// 停止全部监听并清空池，但<b>保留实例可复用</b>。
    /// <para>
    /// 供 <c>IModuleHostedService.StopAsync</c> 使用：模块 Deactivate 后再次 Activate 必须能重新订阅。
    /// 直接用 <see cref="Dispose"/> 会把单例池标为已释放，后续 <see cref="Subscribe"/> 拿到的
    /// watcher 已 Dispose，文件变更静默停更。
    /// </para>
    /// </summary>
    public void StopAll()
    {
        foreach (var (_, e) in _roots)
            e.Watcher.Dispose();

        _roots.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
            return;

        StopAll();
    }

    private static string Normalize(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
