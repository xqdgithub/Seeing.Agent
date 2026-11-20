using System.Text;
using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 单活动的内存可变态（internal）。
/// <para>
/// 所有字段的读写都必须在该活动的 <c>_sync</c> 临界区内完成（<see cref="BackgroundActivityManager"/> 持有），
/// 唯一的例外是 <see cref="TerminalSignal"/> 的等待端与 <see cref="PersistChain"/> 的续体。
/// </para>
/// </summary>
internal sealed class BackgroundActivityRecord
{
    public BackgroundActivityRecord(
        BackgroundActivitySnapshot snapshot,
        CancellationTokenSource cts,
        BackgroundActivityHandle handle,
        string? launcherKind,
        string? dedupeKey)
    {
        Snapshot = snapshot;
        Cts = cts;
        Handle = handle;
        LauncherKind = launcherKind;
        DedupeKey = dedupeKey;
    }

    /// <summary>当前快照（每次状态变更整体替换，<see cref="BackgroundActivitySnapshot.Seq"/> 单调 ++）。</summary>
    public BackgroundActivitySnapshot Snapshot { get; set; }

    /// <summary>manager 持有的取消源（链接超时与宿主停止）；不由 launcher 创建。</summary>
    public CancellationTokenSource Cts { get; }

    /// <summary>下发给 launcher 的唯一权威句柄。</summary>
    public BackgroundActivityHandle Handle { get; }

    /// <summary>登记该 kind 的 launcher 标识；<c>null</c> 表示登记型活动（无 launcher）。</summary>
    public string? LauncherKind { get; }

    /// <summary>去重键（只参与 manager 内部去重，不进 <c>Attributes</c>）。</summary>
    public string? DedupeKey { get; }

    /// <summary>终态是否已 CAS 提交（一旦为 <see langword="true"/> 永不复位）。</summary>
    public bool TerminalCommitted { get; set; }

    /// <summary>超时定时器；进入终态时释放。</summary>
    public ITimer? TimeoutTimer { get; set; }

    /// <summary>宿主停止 token 的登记；进入终态时释放。</summary>
    public CancellationTokenRegistration HostRegistration { get; set; }

    /// <summary>快照落盘与事件发布的串行链（保证「先落盘再发事件」）；结果为该环是否落盘成功。</summary>
    public Task<bool> PersistChain { get; set; } = Task.FromResult(true);

    /// <summary>终态广播（等待方通过它拿到终态快照）。</summary>
    public TaskCompletionSource<BackgroundActivitySnapshot> TerminalSignal { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>上一次发出 <c>activity.progress</c> 事件的时刻（节流用，不落盘）。</summary>
    public DateTimeOffset? LastProgressEventAt { get; set; }

    /// <summary>
    /// 实时输出尾部容量（字符）。只供 UI 增量展示：有界、<b>不落盘</b>、不进快照。
    /// </summary>
    internal const int OutputTailCapacity = 4096;

    private readonly Decoder _outputTailDecoder = Encoding.UTF8.GetDecoder();

    private readonly StringBuilder _outputTail = new(OutputTailCapacity);

    /// <summary>尾部缓冲的写锁（与 manager 的 <c>_sync</c> 独立：读尾部不要求持有状态机锁）。</summary>
    private readonly object _outputTailSync = new();

    /// <summary>当前输出尾部（最后 ≤ <see cref="OutputTailCapacity"/> 个字符）。不落盘。</summary>
    public string OutputTail
    {
        get
        {
            lock (_outputTailSync)
                return _outputTail.ToString();
        }
    }

    /// <summary>
    /// 把一段已落盘的 UTF-8 输出追加进尾部缓冲，超出容量时从头部裁剪。
    /// <para>解码跨块保存状态：多字节字符被切在两个 chunk 之间时不会被拆成替换符。</para>
    /// </summary>
    public void AppendOutputTail(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return;

        Span<char> buffer = utf8.Length <= 256
            ? stackalloc char[utf8.Length]
            : new char[utf8.Length];

        int written;
        lock (_outputTailSync)
        {
            // 状态推进与缓冲写入同锁：并发追加不会交叉解码。
            written = _outputTailDecoder.GetChars(utf8, buffer, flush: false);
            if (written == 0)
                return;

            _outputTail.Append(buffer[..written]);
            TrimLocked();
        }
    }

    /// <summary>超出容量时从头部裁剪；优先裁到整行边界，避免把最后一行切成半截。</summary>
    private void TrimLocked()
    {
        if (_outputTail.Length <= OutputTailCapacity)
            return;

        var overflow = _outputTail.Length - OutputTailCapacity;

        // 优先按行裁剪：从溢出点向后找第一个换行，整行丢弃。
        var cut = overflow;
        var newline = -1;
        for (var i = overflow; i < _outputTail.Length; i++)
        {
            if (_outputTail[i] != '\n')
                continue;

            newline = i;
            break;
        }

        if (newline >= 0 && newline + 1 < _outputTail.Length)
            cut = newline + 1;

        // 不切碎代理对（emoji 等）：落在低位代理上时后移一位。
        if (cut < _outputTail.Length && char.IsLowSurrogate(_outputTail[cut]))
            cut++;

        _outputTail.Remove(0, Math.Min(cut, _outputTail.Length));
    }
}
