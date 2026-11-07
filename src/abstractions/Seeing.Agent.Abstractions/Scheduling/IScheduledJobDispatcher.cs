using Seeing.Agent.Abstractions.Reminders;

namespace Seeing.Agent.Abstractions.Scheduling;

/// <summary>调度任务结果投递抽象（Scheduler / Gateway 等实现）。</summary>
public interface IScheduledJobDispatcher
{
    /// <summary>投递一次调度结果。</summary>
    Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default);
}

/// <summary>投递请求。</summary>
public sealed class DispatchRequest
{
    public required string Source { get; init; }
    public required string TaskType { get; init; }
    public required string Content { get; init; }
    public string? UserInput { get; init; }
    public string? Channel { get; init; }
    public string? UserId { get; init; }
    public string? SessionId { get; init; }

    /// <summary>任务级 conversation 出站目标（优先于会话 <c>ChannelRoute</c>；出站投递器消费）。</summary>
    public DispatchConversation? Conversation { get; init; }

    /// <summary>提醒信封 kind（cron / heartbeat）。系统提示与助手结果都按它标记。</summary>
    public string ReminderKind { get; init; } = SystemReminder.Kinds.Cron;

    /// <summary>系统发送者标签（cron / heartbeat）。写入会话消息的 <c>Sender.Id</c>。</summary>
    public string SenderId { get; init; } = "cron";

    /// <summary>会话写回布局。默认 <see cref="DispatchLayout.PromptAndResult"/>，与既有 cron 行为一致。</summary>
    public DispatchLayout Layout { get; init; } = DispatchLayout.PromptAndResult;

    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// 投递 conversation 目标（出站推送路由）。ChannelId / Kind / ExternalId 齐备才生效。
/// </summary>
public sealed record DispatchConversation
{
    public required string ChannelId { get; init; }

    /// <summary>对话种类稳定字符串（direct / group / guild）</summary>
    public required string Kind { get; init; }

    public required string ExternalId { get; init; }

    public string? Title { get; init; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ChannelId)
        && !string.IsNullOrWhiteSpace(Kind)
        && !string.IsNullOrWhiteSpace(ExternalId);
}

/// <summary>投递结果。</summary>
public sealed class DispatchResult
{
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    public static DispatchResult Ok() => new();
    public static DispatchResult Fail(string error) => new() { Success = false, Error = error };
}
