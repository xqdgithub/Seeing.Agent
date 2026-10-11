using Seeing.Agent.Abstractions.Models;

namespace Seeing.Agent.Abstractions.Runtime;

/// <summary>进上下文身份：User=平权用户消息；System=system-reminder 信封；Assistant=外部产出写回；None=不进 LLM 历史（可持久化展示）。</summary>
public enum InboxPresentation { User, System, Assistant, None }

/// <summary>激活语义：Inject=只落不跑；Wake=落并激活消费；Notify=只出 UI 通知。</summary>
public enum InboxDelivery { Inject, Wake, Notify }

public sealed record InboxMessage
{
    public required string TargetSessionId { get; init; }
    public required InboxSender Sender { get; init; }
    public string? Body { get; init; }
    public InboxDelivery Delivery { get; init; } = InboxDelivery.Wake;
    public InboxPresentation Presentation { get; init; } = InboxPresentation.User;
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }
    public string? SourceTag { get; init; }
    public string? Kind { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? AgentId { get; init; }
    public string? ModelId { get; init; }
}

public sealed record InboxResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? MessageId { get; init; }
    /// <summary>本轮消费产生的执行 Id（尽力而为，可能为空：循环异步启动）。</summary>
    public string? ExecutionId { get; init; }
    public int PendingInputCount { get; init; }

    public static InboxResult Ok(string? messageId = null, string? executionId = null, int pendingInputCount = 0)
        => new() { Success = true, MessageId = messageId, ExecutionId = executionId, PendingInputCount = pendingInputCount };

    public static InboxResult Failed(string error)
        => new() { Success = false, Error = error };
}
