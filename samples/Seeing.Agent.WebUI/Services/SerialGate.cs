namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 轻量串行闸：用 <see cref="Monitor"/> 实现的互斥作用域。
/// <para>
/// 替代 <c>SemaphoreSlim.Wait()</c> 的同步等待：临界区为纯内存读-改-写（微秒级），
/// 用 Monitor 省掉 <see cref="SemaphoreSlim"/> 的异步等待状态机与内核对象开销。
/// </para>
/// <para>
/// <b>语义差异</b>：Monitor 可重入，<see cref="SemaphoreSlim"/> 不可重入。这是有意的变更 ——
/// 聚合器内存在嵌套进入同一临界区的可能，可重入可避免自死锁。
/// </para>
/// <para>
/// <b>注意</b>：Monitor 仍会阻塞线程。本类相对 <c>SemaphoreSlim.Wait()</c> 的收益是去掉
/// 异步等待状态机与 <see cref="Task"/> 分配，而不是「不阻塞」。
/// </para>
/// </summary>
public sealed class SerialGate
{
    private readonly object _gate = new();

    /// <summary>进入临界区；<c>using</c> 结束时自动释放（支持重入）。</summary>
    public Scope Enter()
    {
        Monitor.Enter(_gate);
        return new Scope(_gate);
    }

    /// <summary>临界区作用域。</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly object? _gate;

        internal Scope(object gate) => _gate = gate;

        public void Dispose()
        {
            if (_gate is not null)
                Monitor.Exit(_gate);
        }
    }
}
