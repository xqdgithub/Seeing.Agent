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

    /// <summary>并发通道 ID（Gateway 用于队列隔离/出站回填）。</summary>
    public string? ChannelId { get; init; }

    /// <summary>用户 ID（Gateway 用于审计/出站回填）。</summary>
    public string? UserId { get; init; }

    /// <summary>思考强度档位 key（可选，覆盖会话 SelectedThinkingEffort）。</summary>
    public string? ThinkingEffort { get; init; }

    /// <summary>ACP Mode ID（可选）。</summary>
    public string? ModeId { get; init; }

    /// <summary>工作目录（可选，覆盖会话默认）。</summary>
    public string? WorkingDirectory { get; init; }
}

public sealed record InboxResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? MessageId { get; init; }
    public int PendingInputCount { get; init; }

    public static InboxResult Ok(string? messageId = null, int pendingInputCount = 0)
        => new() { Success = true, MessageId = messageId, PendingInputCount = pendingInputCount };

    public static InboxResult Failed(string error)
        => new() { Success = false, Error = error };
}
