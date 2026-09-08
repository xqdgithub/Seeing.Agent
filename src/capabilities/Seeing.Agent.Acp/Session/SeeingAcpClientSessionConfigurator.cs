using Seeing.Agent.Acp.Client;

namespace Seeing.Agent.Acp.Session;

/// <summary>
/// <see cref="SeeingAcpClient"/> 的 session config 适配器。
/// </summary>
public sealed class SeeingAcpClientSessionConfigurator(SeeingAcpClient client) : IAcpSessionConfigClient
{
    public Task SetConfigOptionAsync(
        string sessionId,
        string configId,
        string value,
        CancellationToken cancellationToken = default) =>
        client.SessionSetConfigOptionAsync(sessionId, configId, value, cancellationToken);

    public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default) =>
        client.SessionSetModeAsync(sessionId, modeId, cancellationToken);

    public Task SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken = default) =>
        client.SessionSetModelAsync(sessionId, modelId, cancellationToken);
}
