namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 后台活动快照——活动状态与控制面的唯一真相。
/// <para>
/// 扁平记录 + 能力位 + 开放属性：不按 <c>Kind</c> 派生子类型，保证落盘、跨进程投递与
/// 多宿主回放的向后兼容；<c>Kind</c> 专属信息走 <see cref="Attributes"/>（仅展示/诊断）
/// 与 <see cref="Result"/>（强类型引用）。
/// </para>
/// <para>
/// 框架决策只能读取 <see cref="Capabilities"/>，不得读取 <see cref="Attributes"/>。
/// </para>
/// </summary>
public sealed record BackgroundActivitySnapshot
{
    // ── Identity（不可变） ─────────────────────────────────────────────

    /// <summary>活动 id（<c>act_</c> 前缀）。</summary>
    public required string Id { get; init; }

    /// <summary>活动种类（开放字符串，带命名空间前缀）。</summary>
    public required string Kind { get; init; }

    /// <summary>展示标题。</summary>
    public required string Title { get; init; }

    /// <summary>发起该活动的工具调用 id（UI 据此与工具卡片关联）。</summary>
    public string? OriginToolCallId { get; init; }

    /// <summary>发起来源。</summary>
    public BackgroundActivityOrigin Origin { get; init; }

    /// <summary>活动所属工作区根。</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>宿主实例标识（pid + 进程启动时间）。</summary>
    public required string HostInstanceId { get; init; }

    /// <summary>创建时刻。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    // ── Ownership（不可变） ───────────────────────────────────────────

    /// <summary>归属会话 id。</summary>
    public required string OwnerSessionId { get; init; }

    /// <summary>归属执行 id（工具发起为本轮执行 id；Scheduler 旁路为本次 run id）。</summary>
    public required string OwnerExecutionId { get; init; }

    /// <summary>父活动 id（取消沿树级联）。</summary>
    public string? ParentActivityId { get; init; }

    /// <summary>目标会话 id（<c>kind=agent-session</c> 时使用）。</summary>
    public string? TargetSessionId { get; init; }

    // ── State（可变，Seq 单调） ───────────────────────────────────────

    /// <summary>当前状态。</summary>
    public BackgroundActivityStatus Status { get; init; }

    /// <summary>状态版本号，每次变更自增；订阅方必须丢弃更旧的快照。</summary>
    public long Seq { get; init; }

    /// <summary>实际开始时刻。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>最后一次心跳时刻。</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>进入终态的时刻。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>结果被发起方观察到的时刻（非空时不再投递唤醒）。</summary>
    public DateTimeOffset? ResultObservedAt { get; init; }

    /// <summary>进度（不落盘，仅事件）。</summary>
    public BackgroundActivityProgress? Progress { get; init; }

    /// <summary>已写入输出的字节数（实际落盘值，不含截断标记）；尚未写入任何输出时为 <see langword="null"/>。</summary>
    public long? OutputBytes { get; init; }

    /// <summary>输出是否已因达到 <see cref="OutputLimitBytes"/> 被截断。</summary>
    public bool OutputTruncated { get; init; }

    /// <summary>取消原因（仅 <see cref="BackgroundActivityStatus.Cancelled"/> 时有值）。</summary>
    public BackgroundActivityCancelReason? CancelReason { get; init; }

    // ── Capabilities（唯一可驱动框架行为） ────────────────────────────

    /// <summary>能力位。</summary>
    public BackgroundActivityCapabilities Capabilities { get; init; }

    // ── Result（仅终态，不内联内容） ─────────────────────────────────

    /// <summary>结果引用（不含输出正文）。</summary>
    public BackgroundActivityResult? Result { get; init; }

    // ── Policy ───────────────────────────────────────────────────────

    /// <summary>完成通知策略。</summary>
    public BackgroundActivityNotifyPolicy NotifyPolicy { get; init; }

    /// <summary>活动超时。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>输出上限（字节）。</summary>
    public long OutputLimitBytes { get; init; }

    /// <summary>可见范围。</summary>
    public BackgroundActivityVisibility Visibility { get; init; }

    /// <summary>生命周期边界。</summary>
    public BackgroundActivityLifetime Lifetime { get; init; }

    // ── 展示与诊断；框架决策不得读取 ──────────────────────────────────

    /// <summary>kind 专属的展示/诊断信息（建议键：<c>pid</c> / <c>exitCode</c> / <c>workdir</c> / <c>endpoint</c>）。</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>活动进度（只发节流事件，不落盘）。</summary>
public sealed record BackgroundActivityProgress(
    double? Fraction,
    string? UnitsName,
    long? UnitsDone,
    long? UnitsTotal,
    string? Message);

/// <summary>活动终态结果引用（不内联输出正文）。</summary>
public sealed record BackgroundActivityResult(
    BackgroundActivityResultKind Kind,
    string? ArtifactPath,
    string? ResultSessionId,
    int? ExitCode,
    string? ErrorCode,
    string? ErrorMessage);
