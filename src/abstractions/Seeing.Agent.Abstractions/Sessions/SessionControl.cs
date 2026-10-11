using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Sessions;

public enum SessionOwner { Parent, Human, Self }

public sealed record SessionControl
{
    public SessionOwner Owner { get; init; } = SessionOwner.Parent;
    public DateTimeOffset? TakenOverAt { get; init; }
    public string? TakenOverBy { get; init; }
}

public static class SessionControlKeys
{
    public const string Owner = "control.owner";
    public const string TakenOverAt = "control.taken_over_at";
    public const string TakenOverBy = "control.taken_over_by";
    public const string StopReason = "control.stop_reason";
    public const string StopReasonCascadedFromParent = "cascaded_from_parent_stop";
}

public static class SessionControlExtensions
{
    public static SessionControl GetControl(this SessionData session)
    {
        var owner = SessionOwner.Parent;
        if (session.Metadata.TryGetValue(SessionControlKeys.Owner, out var raw)
            && Enum.TryParse<SessionOwner>(raw, ignoreCase: true, out var parsed))
            owner = parsed;

        DateTimeOffset? takenAt = null;
        if (session.Metadata.TryGetValue(SessionControlKeys.TakenOverAt, out var at)
            && DateTimeOffset.TryParse(at, out var parsedAt))
            takenAt = parsedAt;

        session.Metadata.TryGetValue(SessionControlKeys.TakenOverBy, out var by);
        return new SessionControl { Owner = owner, TakenOverAt = takenAt, TakenOverBy = by };
    }

    public static void SetOwner(this SessionData session, SessionOwner owner, string? by)
    {
        session.Metadata[SessionControlKeys.Owner] = owner.ToString().ToLowerInvariant();
        if (owner == SessionOwner.Human)
        {
            session.Metadata[SessionControlKeys.TakenOverAt] = DateTimeOffset.UtcNow.ToString("O");
            if (!string.IsNullOrEmpty(by))
                session.Metadata[SessionControlKeys.TakenOverBy] = by;
        }
        else
        {
            session.Metadata.Remove(SessionControlKeys.TakenOverAt);
            session.Metadata.Remove(SessionControlKeys.TakenOverBy);
        }
    }
}
