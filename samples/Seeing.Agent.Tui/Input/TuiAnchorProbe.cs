namespace Seeing.Agent.Tui.Input;

/// <summary>
/// DSR（<c>ESC[6n</c>）光标底锚探针：把「渲染侧发一次查询 → 输入侧收一次回复」配对为最新 (绝对行, 代次)。
/// <para>
/// 因终端输出为单写者（渲染线程/模态控件），DSR 严格「发一个、收一个才发下一个」，故 pending 唯一、无并发错配。
/// 命中方读 <see cref="TryGet"/>，并须与随附命中表的 <c>FrameGen</c> 同代次才生效（跨代次/未校准即失效回落键盘）。
/// </para>
/// <para>光标报告经此旁路，<b>绝不</b>进入按键/relay/engine 通道（避免回灌唤醒主循环致满帧自激）。</para>
/// </summary>
public interface ITuiAnchorProbe
{
    /// <summary>收到 <c>ESC[row;colR</c> 时由输入层调用，落 (row, 当前 pending 代次)。</summary>
    void Report(int row);

    /// <summary>读最新已配对的 (绝对行, 代次)；未校准时返回 false。</summary>
    bool TryGet(out int row, out long gen);

    /// <summary>置为未知（如终端 resize）：下一次命中在重新校准前失效。</summary>
    void Invalidate();

    /// <summary>登记一次探测并写 <c>ESC[6n</c>（由渲染线程调用）；返回该帧代次 <c>gen</c>。</summary>
    long BeginProbe(Action writeDsr);

    /// <summary>
    /// 以<b>外部指定的代次</b>登记探测（engine 每帧自增分配 gen，随 <c>UpdateAsync</c> 传到渲染线程，
    /// 使底锚代次与命中表 <c>FrameGen</c> 同源；渲染线程在 <c>UpdateTarget</c> 后、<c>PlaceCaret</c> 前调用）。
    /// </summary>
    void BeginProbe(long gen, Action writeDsr);
}

/// <summary><see cref="ITuiAnchorProbe"/> 默认实现（线程安全：渲染线程 BeginProbe、输入线程 Report）。</summary>
public sealed class TuiAnchorProbe : ITuiAnchorProbe
{
    private readonly Lock _gate = new();
    private long _gen;
    private readonly Queue<long> _pending = new();
    private int _row;
    private long _rowGen;
    private bool _hasRow;

    public long BeginProbe(Action writeDsr)
    {
        ArgumentNullException.ThrowIfNull(writeDsr);

        long gen;
        lock (_gate)
        {
            gen = ++_gen;
            EnqueuePendingLocked(gen);
        }

        writeDsr();
        return gen;
    }

    public void BeginProbe(long gen, Action writeDsr)
    {
        ArgumentNullException.ThrowIfNull(writeDsr);

        lock (_gate)
        {
            if (gen > _gen)
                _gen = gen;
            EnqueuePendingLocked(gen);
        }

        writeDsr();
    }

    public void Report(int row)
    {
        lock (_gate)
        {
            // FIFO：DSR 按请求顺序回复，弹出队首代次与该回复配对；无在途请求（未探测/多余回复）则丢弃。
            if (_pending.Count == 0)
                return;

            var gen = _pending.Dequeue();
            _row = row;
            _rowGen = gen;
            _hasRow = true;
        }
    }

    public bool TryGet(out int row, out long gen)
    {
        lock (_gate)
        {
            row = _row;
            gen = _rowGen;
            return _hasRow;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _hasRow = false;
            _pending.Clear();
        }
    }

    private void EnqueuePendingLocked(long gen)
    {
        _pending.Enqueue(gen);
        // 终端若长期不回 DSR：限制在途深度，丢弃最旧（其配对将因代次不符而安全失效，且内存有界）。
        if (_pending.Count > 16)
            _pending.Dequeue();
    }
}

/// <summary>无操作探针（降级/无鼠标）：<see cref="TryGet"/> 恒 false，命中安全失效。</summary>
public sealed class NullTuiAnchorProbe : ITuiAnchorProbe
{
    public void Report(int row) { }

    public bool TryGet(out int row, out long gen)
    {
        row = 0;
        gen = 0;
        return false;
    }

    public void Invalidate() { }

    public long BeginProbe(Action writeDsr) => 0;

    public void BeginProbe(long gen, Action writeDsr) { /* 无操作探针：不发 DSR（调用方已按 MouseEnabled 门控） */ }
}
