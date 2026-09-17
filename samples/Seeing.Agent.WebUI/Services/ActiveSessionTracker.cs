using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.WebUI.Services;

public sealed class ActiveSessionTracker
{
    private readonly IPermissionPresenceStore _presence;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _sessionByCircuit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _circuitCountBySession = new(StringComparer.Ordinal);

    public ActiveSessionTracker(IPermissionPresenceStore presence)
    {
        _presence = presence ?? throw new ArgumentNullException(nameof(presence));
    }

    public void Attach(string sessionId, string circuitId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        circuitId ??= string.Empty;

        string? previous = null;
        bool attach;
        lock (_gate)
        {
            _sessionByCircuit.TryGetValue(circuitId, out previous);
            if (string.Equals(previous, sessionId, StringComparison.Ordinal))
                return;

            if (previous != null)
                ReleaseForCircuit(previous, circuitId);

            _sessionByCircuit[circuitId] = sessionId;
            attach = Increment(sessionId);
        }

        if (previous != null && !IsActive(previous))
            _presence.Detach(previous);
        if (attach)
            _presence.Attach(sessionId);
    }

    public void Detach(string sessionId, string circuitId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        circuitId ??= string.Empty;

        bool present;
        lock (_gate)
        {
            if (!_sessionByCircuit.TryGetValue(circuitId, out var current) ||
                !string.Equals(current, sessionId, StringComparison.Ordinal))
                return;

            ReleaseForCircuit(sessionId, circuitId);
            present = IsActiveUnlocked(sessionId);
        }

        if (!present)
            _presence.Detach(sessionId);
    }

    public void DetachCircuit(string circuitId)
    {
        circuitId ??= string.Empty;

        string? session;
        bool present;
        lock (_gate)
        {
            if (!_sessionByCircuit.TryGetValue(circuitId, out session))
                return;

            ReleaseForCircuit(session, circuitId);
            present = IsActiveUnlocked(session);
        }

        if (!present)
            _presence.Detach(session);
    }

    public bool IsActive(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        lock (_gate)
            return IsActiveUnlocked(sessionId);
    }

    private bool Increment(string sessionId)
    {
        _circuitCountBySession.TryGetValue(sessionId, out var count);
        _circuitCountBySession[sessionId] = count + 1;
        return count == 0;
    }

    private void ReleaseForCircuit(string sessionId, string circuitId)
    {
        _sessionByCircuit.Remove(circuitId);

        if (!_circuitCountBySession.TryGetValue(sessionId, out var count))
            return;

        if (count <= 1)
            _circuitCountBySession.Remove(sessionId);
        else
            _circuitCountBySession[sessionId] = count - 1;
    }

    private bool IsActiveUnlocked(string sessionId) =>
        _circuitCountBySession.TryGetValue(sessionId, out var count) && count > 0;
}
