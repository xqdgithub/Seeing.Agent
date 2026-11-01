namespace Seeing.Agent.Abstractions.Execution;

using Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 一轮执行进入终态后的**事实快照**（不可变聚合体）。
/// <para>
/// 这是"这一轮到底发生了什么"的唯一权威叙述：由执行链在终态处一次性组装，
/// 供终态观察者（记忆轮次触发、自动反思等）消费。
/// </para>
/// <para>
/// 与 <see cref="ExecutionRecord"/> 的分工：<see cref="ExecutionRecord"/> 是可变的生命周期记录
/// （执行中逐字段演进、含取消令牌等运行时物件）；本类型只在终态后产生，不含任何可变状态。
/// </para>
/// <para>
/// 约束：<b>只陈述已发生的事实</b>，不得携带推测值或占位值。任何字段若无法确定，保持
/// <c>null</c> / 默认值，而不是伪造一个看起来合理的数字。
/// </para>
/// </summary>
public sealed record ExecutionOutcome
{
    /// <summary>所属会话 ID。</summary>
    public required string SessionId { get; init; }

    /// <summary>本轮执行 ID（与 <see cref="ExecutionRecord.ExecutionId"/> 同源）。</summary>
    public required string ExecutionId { get; init; }

    /// <summary>终态状态；正常情况下只会是 <see cref="ExecutionStatus.Completed"/>、<see cref="ExecutionStatus.Failed"/> 或 <see cref="ExecutionStatus.Cancelled"/>。</summary>
    public required ExecutionStatus Status { get; init; }

    /// <summary>
    /// 本轮实际执行的 Agent 名称；未真正执行 Agent（如命令短路、纯本地回复）时为 <c>null</c>。
    /// <para>与 <see cref="AgentExecuted"/> 配合使用：名称为空不代表没执行，须以标志位为准。</para>
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// 本轮是否真正进入了 Agent 执行（<c>true</c>），或仅走了命令处理 / 早退路径（<c>false</c>）。
    /// <para>观察者据此排除"没有产生模型调用"的轮次，避免把空轮次计入记忆或反思节奏。</para>
    /// </summary>
    public bool AgentExecuted { get; init; }

    /// <summary>
    /// 本轮是否属于顶层会话（用户主会话）。
    /// <para>
    /// <b>真值来源为 <c>SessionData.Kind</c></b>（<c>SessionKind.SubAgent</c> → <c>false</c>）。
    /// <b>不得</b>读 <c>ChatExecutionContext.IsTopLevel</c>——该字段在唯一构造点从未被赋值，
    /// 恒为默认 <c>true</c>，是既存缺陷，不能作为判定依据。
    /// </para>
    /// </summary>
    public bool IsTopLevel { get; init; }

    /// <summary>
    /// 父会话 ID（子 Agent 会话才有值）；顶层会话为 <c>null</c>。
    /// <para>与 <see cref="IsTopLevel"/> 同源，均取自会话组的父子关系。</para>
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// 本轮模型循环的总步数；无模型调用（<see cref="AgentExecuted"/> 为 <c>false</c>）时为 0。
    /// </summary>
    public int TotalSteps { get; init; }

    /// <summary>
    /// 本轮 Token 使用量合计；无模型调用或上游未上报时为 <c>null</c>。
    /// <para>为 <c>null</c> 表示"未知"，不等价于"零消耗"。</para>
    /// </summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>本轮执行耗时（模型循环累计时长）；未知时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>进入终态的时刻（UTC）；在终态处组装时取当时时间。</summary>
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}
