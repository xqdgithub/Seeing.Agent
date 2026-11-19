using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 帧泵：只保留最新帧，串行广播给观看者，并保证每一帧的 ack 恰好执行一次。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么只保留最新帧</b>：慢客户端（后台标签页、网络抖动）会让帧在内存里堆积，
/// 几分钟就能吃掉几百 MB；而且堆积的帧全是过期画面。丢弃是安全的——帧是幂等的画面快照。
/// </para>
/// <para>
/// <b>为什么 ack 跟着帧走</b>：ack 晚发没关系（Chromium 只是暂时不推新帧），
/// 但绝不能漏。被覆盖的帧在覆盖的那一刻 ack；真正广播的帧在 <c>finally</c> 里 ack
/// （广播抛异常也要 ack，否则一次异常就让画面永久冻结）。
/// </para>
/// <para>
/// <b>为什么不阻塞 CDP 回调</b>：<see cref="Offer"/> 只做一次加锁与一次入列，
/// CDP 回调阻塞会连带卡住整个浏览器目标。
/// </para>
/// </remarks>
internal sealed class FramePump
{
    private readonly Action<BrowserFrame> _broadcast;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    private BrowserFrame? _pending;
    private Action? _pendingAck;
    private bool _flushing;
    private Task? _flushLoop;

    /// <summary>创建帧泵。</summary>
    /// <param name="broadcast">把一帧广播给观看者（内部应逐观看者兜底）。</param>
    /// <param name="logger">日志（可空）。</param>
    public FramePump(Action<BrowserFrame> broadcast, ILogger? logger = null)
    {
        _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
        _logger = logger;
    }

    /// <summary>创建一个只用于测试的泵（广播动作由调用方给定）。</summary>
    internal static FramePump CreateForTest(Action<BrowserFrame> broadcast) => new(broadcast);

    /// <summary>
    /// 交出一帧。若上一帧还没被广播，它会被覆盖并<b>立即 ack</b>。
    /// </summary>
    /// <param name="frame">新帧。</param>
    /// <param name="ack">该帧的 ack（保证恰好执行一次；抛异常会被吞掉）。</param>
    public void Offer(BrowserFrame frame, Action ack)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(ack);

        var once = AckOnce(ack);
        Action? replaced;

        lock (_lock)
        {
            // 覆盖旧帧：宁可丢中间帧，也不要堆积。
            // 注意不能在这里 return —— 泵正在跑时更要 ack 被覆盖的那一帧（见下方 SafeAck）。
            replaced = _pendingAck;
            _pending = frame;
            _pendingAck = once;

            if (!_flushing)
            {
                _flushing = true;
                _flushLoop = Task.Run(FlushAsync);
            }
        }

        // 被覆盖的帧也要 ack：那一帧的 Chromium 帧窗口必须释放，
        // 否则窗口计数被占满，后续帧不再到达。
        SafeAck(replaced);
    }

    /// <summary>
    /// 等到「当前所有已交付的帧都处理完」。
    /// </summary>
    /// <remarks>
    /// 仅供测试与关停路径使用：正常运行时没有任何调用方需要等泵。
    /// 用完成信号而不是固定等待，测试才不会依赖机器负载。
    /// </remarks>
    internal async Task DrainAsync()
    {
        while (true)
        {
            Task? loop;
            lock (_lock)
            {
                // 兜底：上一轮循环可能在「泵刚退出、又有新帧进来」的窗口里观察到 null。
                if (_pending is not null && !_flushing)
                {
                    _flushing = true;
                    _flushLoop = Task.Run(FlushAsync);
                }

                loop = _flushLoop;
            }

            if (loop is null)
                return;

            await loop.ConfigureAwait(false);
        }
    }

    private async Task FlushAsync()
    {
        while (true)
        {
            BrowserFrame? frame;
            Action? ack;

            lock (_lock)
            {
                frame = _pending;
                ack = _pendingAck;
                _pending = null;
                _pendingAck = null;

                if (frame is null || ack is null)
                {
                    _flushing = false;
                    _flushLoop = null;
                    return;
                }
            }

            try
            {
                _broadcast(frame);
            }
            catch (Exception ex)
            {
                // 逐观看者兜底在 Broadcast 内部；走到这里说明广播本身坏了（如页面已关）。
                // 异常绝不能逃逸：逃逸会跳出循环，剩下的帧永远不 ack。
                _logger?.LogDebug(ex, "广播画面帧失败（忽略）");
            }
            finally
            {
                // 恰好一次：成功、失败、被覆盖都由这里兜住
                SafeAck(ack);
            }

            // 让出线程：高帧率下避免占满线程池
            await Task.Yield();
        }
    }

    private void SafeAck(Action? ack)
    {
        if (ack is null)
            return;

        try
        {
            ack();
        }
        catch (Exception ex)
        {
            // ack 失败 = CDP 已断开；此时画面流本来也要重建，不能让它打断泵
            _logger?.LogDebug(ex, "画面帧 ack 失败（忽略）");
        }
    }

    /// <summary>把 ack 包装成「最多执行一次」：双重保险，任何路径都只能 ack 一遍。</summary>
    private static Action AckOnce(Action ack)
    {
        var done = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
                ack();
        };
    }
}
