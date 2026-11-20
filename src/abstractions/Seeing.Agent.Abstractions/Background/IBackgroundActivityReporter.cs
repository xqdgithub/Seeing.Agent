namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 活动执行方上报端口，由 manager 实现并通过 <see cref="BackgroundActivityHandle"/> 下发。
/// <para>
/// 终态只能由 manager 的 CAS 写入：取消先行落 <c>Cancelled</c>，后续 <see cref="Complete"/> /
/// <see cref="Fail"/> 被丢弃；活动进入终态后本端口所有方法均为 no-op。
/// </para>
/// </summary>
public interface IBackgroundActivityReporter
{
    /// <summary>声明"真正开始"（用于延后进入 <see cref="BackgroundActivityStatus.Running"/>）。</summary>
    void ReportStarted();

    /// <summary>上报进度（不落盘，只发节流事件）。</summary>
    void ReportProgress(BackgroundActivityProgress progress);

    /// <summary>心跳（至少每 30s 一次；超过 3 个周期未更新 UI 标"疑似卡死"）。</summary>
    void Heartbeat();

    /// <summary>追加输出（字节口径，按活动输出上限截断）。</summary>
    Task AppendOutputAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default);

    /// <summary>上报成功终态（同步落盘 + 写 outbox 后才返回）。</summary>
    void Complete(BackgroundActivityResult result);

    /// <summary>上报失败终态（同步落盘 + 写 outbox 后才返回）。</summary>
    void Fail(string errorCode, string errorMessage);
}
