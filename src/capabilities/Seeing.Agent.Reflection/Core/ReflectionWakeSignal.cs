using System;
using System.Threading;
using System.Threading.Tasks;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 复盘后台的「立即处理」信号。
/// <para>
/// 没有它时，手动触发的请求最长要等到下一次 1 分钟 tick 才会被执行——
/// 用户点了按钮、页面却说「稍后查看」，而实际上一分钟后才开始跑，观感像没生效。
/// </para>
/// <para>
/// 用「可重复置位的闩」而不是计数器/队列：Worker 每轮都把队列全量排空，
/// 漏掉一次 pulse 也不会漏处理（下一轮仍会看到请求），因此这里只需保证
/// 「有信号就提前醒一次」，多置几次或丢一次都无害。
/// </para>
/// </summary>
public sealed class ReflectionWakeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>请求 Worker 尽快跑一轮。</summary>
    public void Pulse()
    {
        try
        {
            // 已经有未消费的信号时不叠加：满 1 即可，信号是「有事」而不是「有 N 件事」。
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有待处理信号：无需再置。
        }
    }

    /// <summary>
    /// 等待下一次唤醒，最长等 <paramref name="timeout"/>。
    /// </summary>
    /// <returns><c>true</c> 表示被 <see cref="Pulse"/> 唤醒；<c>false</c> 表示超时（正常 tick）。</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
        => _signal.WaitAsync(timeout, ct);
}
