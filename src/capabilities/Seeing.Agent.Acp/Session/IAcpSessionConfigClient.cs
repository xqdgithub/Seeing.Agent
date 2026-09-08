namespace Seeing.Agent.Acp.Session;

/// <summary>
/// ACP session mode/model 设置抽象（便于测试与 <see cref="Client.SeeingAcpClient"/> 解耦）。
/// </summary>
public interface IAcpSessionConfigClient
{
    Task SetConfigOptionAsync(
        string sessionId,
        string configId,
        string value,
        CancellationToken cancellationToken = default);

    Task SetModeAsync(
        string sessionId,
        string modeId,
        CancellationToken cancellationToken = default);

    Task SetModelAsync(
        string sessionId,
        string modelId,
        CancellationToken cancellationToken = default);
}
