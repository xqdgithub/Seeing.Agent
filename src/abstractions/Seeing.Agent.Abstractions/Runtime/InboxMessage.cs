using Seeing.Agent.Abstractions.Channels;
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

    /// <summary>入站对话（出站回填的结构化来源）。</summary>
    public ConversationInfo? Conversation { get; init; }

    /// <summary>被动回复所针对的平台消息 ID。</summary>
    public string? ReplyToMsgId { get; init; }

    /// <summary>平台侧发送者身份（与 <see cref="Sender"/> 的运行时身份并列）。</summary>
    public ChannelUserIdentity? SenderIdentity { get; init; }

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
