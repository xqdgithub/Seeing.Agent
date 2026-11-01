using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;

namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// Represents a record of an execution request, tracking its state throughout its lifecycle.
/// </summary>
public class ExecutionRecord
{
    /// <summary>
    /// Unique identifier for this execution.
    /// </summary>
    public string ExecutionId { get; set; } = string.Empty;

    /// <summary>
    /// The session this execution belongs to.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the execution.
    /// </summary>
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;

    /// <summary>
    /// The input for this execution (user message, attachments).
    /// </summary>
    public ChatInput? Input { get; set; }

    /// <summary>
    /// The options for this execution (agent, model, etc.).
    /// </summary>
    public ChatOptions? Options { get; set; }

    /// <summary>
    /// When the execution was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the execution actually started processing.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the execution reached a terminal state.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Error message if the execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// ID of the last processed message (for recovery purposes).
    /// </summary>
    public string? LastMessageId { get; set; }

    /// <summary>
    /// 本轮实际执行的 Agent 名称。
    /// <para>
    /// 语义与 <see cref="ExecutionOutcome.AgentName"/> 一致：<b>仅在 <see cref="AgentExecuted"/> 为 <c>true</c> 时有值</b>；
    /// 命令短路、纯系统批次等未真正执行 Agent 的轮次必须保持 <c>null</c>，不得填入"将要执行"的名称。
    /// </para>
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// 本轮是否真正进入了 Agent 执行（<c>true</c>），或仅走了命令处理 / 早退路径（<c>false</c>）。
    /// </summary>
    public bool AgentExecuted { get; set; }

    /// <summary>
    /// 本轮模型循环的总步数（多段 <c>LoopCompleteEvent</c> 累计）；未执行 Agent 时为 0。
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    /// 本轮 Token 使用量合计（逐字段累加）；无模型调用或上游未上报时为 <c>null</c>（表示未知，不等价于零消耗）。
    /// </summary>
    public TokenUsage? Usage { get; set; }

    /// <summary>
    /// 本执行专属的取消令牌源。
    /// <para>
    /// 在入队时创建并绑定到执行记录本身：无论该记录成为当前项还是排队项，取消都只作用于本记录。
    /// 队列推进更换“当前项”不会转移取消目标，从而消除 exec 之间的取消串扰（闪断根因）。
    /// </para>
    /// <para>
    /// 释放时机：记录进入终态（<c>CompleteAsync</c>）或队列整体释放；不在取消瞬间释放，避免执行体仍在使用令牌时触发 ObjectDisposedException。
    /// </para>
    /// </summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>
    /// Whether this execution is in a terminal state.
    /// </summary>
    public bool IsTerminal => Status is ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.Cancelled;
}
