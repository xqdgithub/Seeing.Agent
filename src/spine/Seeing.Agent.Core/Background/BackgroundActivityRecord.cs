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
}
