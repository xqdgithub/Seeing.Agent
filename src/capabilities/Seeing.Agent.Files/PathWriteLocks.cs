using System.Collections.Concurrent;

namespace Seeing.Agent.Files;

/// <summary>进程内按绝对路径写序列化（引用计数回收，best-effort）。</summary>
internal sealed class PathWriteLocks
{
    private sealed class Holder
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public int RefCount;
    }

    private readonly ConcurrentDictionary<string, Holder> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxLocks;

    /// <summary>构造；<paramref name="maxLocks"/> 预留给后续淘汰策略。</summary>
    public PathWriteLocks(int maxLocks = 4096) => _maxLocks = maxLocks;

    /// <summary>获取路径锁；调用方负责在 using 结束时 Release。</summary>
    public async Task<IDisposable> AcquireAsync(string path, CancellationToken ct)
    {
        var holder = _locks.GetOrAdd(path, _ => new Holder());
        Interlocked.Increment(ref holder.RefCount);
        await holder.Sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(this, path, holder);
    }

    private void Release(string path, Holder holder)
    {
        holder.Sem.Release();
        if (Interlocked.Decrement(ref holder.RefCount) <= 0 && holder.Sem.CurrentCount == 1)
            _locks.TryRemove(path, out _);
    }

    private sealed class Releaser(PathWriteLocks owner, string path, Holder holder) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner.Release(path, holder);
        }
    }
}
