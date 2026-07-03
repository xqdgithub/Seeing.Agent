using Seeing.Agent.Acp.Client;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// 协调用户取消与 ACP SessionCancelAsync。
/// </summary>
public sealed class AcpCancellationCoordinator
{
    private readonly Dictionary<string, CancellationTokenRegistration> _registrations = new();
    private readonly object _lock = new();

    public IDisposable Register(
        string seeingSessionId,
        string acpSessionId,
        SeeingAcpClient client,
        CancellationToken cancellationToken)
    {
        var key = $"{seeingSessionId}:{acpSessionId}";
        var registration = cancellationToken.Register(() =>
        {
            _ = client.SessionCancelAsync(acpSessionId, CancellationToken.None);
        });

        lock (_lock)
            _registrations[key] = registration;

        return new CancelScope(this, key);
    }

    private void Unregister(string key)
    {
        lock (_lock)
        {
            if (_registrations.Remove(key, out var registration))
                registration.Dispose();
        }
    }

    private sealed class CancelScope(AcpCancellationCoordinator coordinator, string key) : IDisposable
    {
        public void Dispose() => coordinator.Unregister(key);
    }
}
