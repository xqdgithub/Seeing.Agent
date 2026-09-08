namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// Gateway HTTP/WebSocket 服务生命周期管理。
/// </summary>
public interface IGatewayServer
{
    /// <summary>Gateway 是否正在运行</summary>
    bool IsRunning { get; }

    /// <summary>启动 Gateway Kestrel 服务</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停止 Gateway Kestrel 服务</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
