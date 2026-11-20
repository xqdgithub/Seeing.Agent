namespace Seeing.Agent.Core.Background;

/// <summary>outbox 批次状态：<c>Pending</c> 待投递；<c>Delivered</c> 已投递或已并入新批次；<c>Dropped</c> 不再投递。</summary>
public enum BackgroundActivityOutboxStatus
{
    Pending,
    Delivered,
    Dropped
}

/// <summary>
/// 一条待投递的完成通知批次（spec §11.1 的 <c>outbox/{digestId}.json</c>）。
/// <para>
/// 合并后的 digest 是**唯一投递单元**：同一归属会话在合并窗口内的多条活动完成合并成一条通知，
/// 重试始终重放同一个 <see cref="DigestId"/>。
/// </para>
/// <para>
/// <see cref="DigestId"/> / <see cref="HostInstanceId"/> / <see cref="CreatedAt"/> 由
/// <see cref="BackgroundActivityOutbox.EnqueueAsync"/> 统一分配：调用方（manager）只负责给出
/// 归属会话与活动 id。
/// </para>
/// </summary>
public sealed record BackgroundActivityNotification
{
    /// <summary>批次 id（<c>dgt_</c> 前缀），同时是幂等键与文件名。</summary>
    public required string DigestId { get; init; }

    /// <summary>写入该批次的宿主实例标识（只有本实例的 pending 会被本实例泵取）。</summary>
    public required string HostInstanceId { get; init; }

    /// <summary>归属会话——也是唤醒消息的目标会话。</summary>
    public required string OwnerSessionId { get; init; }

    /// <summary>本批次覆盖的活动 id。</summary>
    public required IReadOnlyList<string> ActivityIds { get; init; }

    /// <summary>批次状态。</summary>
    public BackgroundActivityOutboxStatus Status { get; init; } = BackgroundActivityOutboxStatus.Pending;

    /// <summary>已失败的投递次数（成功投递不计）。</summary>
    public int Attempts { get; init; }

    /// <summary>最后一次失败的摘要，或终态的处理原因（如 <c>merged_into:...</c> / <c>result_observed</c>）。</summary>
    public string? LastError { get; init; }

    /// <summary>入队时刻（合并窗口按它计算）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
