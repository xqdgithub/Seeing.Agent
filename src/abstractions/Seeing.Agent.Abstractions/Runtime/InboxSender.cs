namespace Seeing.Agent.Abstractions.Runtime;

public enum InboxSenderKind { Human, Session, System }

public sealed record InboxSender
{
    public required InboxSenderKind Kind { get; init; }
    /// <summary>Session：来源会话 Id；System：子系统标签（task/cron/heartbeat/gateway…）。</summary>
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
}
