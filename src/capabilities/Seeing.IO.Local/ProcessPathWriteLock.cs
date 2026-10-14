using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.IO.Local;

/// <summary>
/// 进程内按规范化绝对路径写序列化实现。引用计数回收，避免锁表无界增长。
/// </summary>
public sealed class ProcessPathWriteLock : IPathWriteLock
{
    private sealed class Holder
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public int RefCount;
    }

    private readonly ConcurrentDictionary<string, Holder> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = Normalize(path);
        var holder = _locks.GetOrAdd(key, _ => new Holder());
        Interlocked.Increment(ref holder.RefCount);
        try
        {
            await holder.Sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 获取失败（取消）需回退引用计数，避免泄漏
            ReleaseRef(key, holder, acquired: false);
            throw;
        }
        return new Releaser(this, key, holder);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private void ReleaseRef(string key, Holder holder, bool acquired)
    {
        if (acquired)
            holder.Sem.Release();

        if (Interlocked.Decrement(ref holder.RefCount) <= 0 && holder.Sem.CurrentCount == 1)
            _locks.TryRemove(key, out _);
    }

    private sealed class Releaser(ProcessPathWriteLock owner, string key, Holder holder) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner.ReleaseRef(key, holder, acquired: true);
        }
    }
}
