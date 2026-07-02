using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Gateway.Hosting;

namespace Seeing.Agent.Gateway;

/// <summary>
/// Gateway 插件入口：在 InitializeAsync 内启动独立 Kestrel 服务。
/// </summary>
public class GatewayExtension : IExtension
{
    /// <inheritdoc />
    public string? Id => "seeing.agent.gateway";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public string Name => "Seeing.Agent Gateway";

    /// <inheritdoc />
    public string Description => "Gateway 服务，提供 HTTP+SSE 与 WebSocket 聊天及权限 API";

    /// <inheritdoc />
    public string Target => "server";

    private GatewayHost? _host;
    private ILogger? _logger;

    /// <inheritdoc />
    public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<GatewayExtension>();

        _logger.LogInformation("初始化 {Name} v{Version} (state: {State})", Name, Version, meta.State);

        var gatewayOptions = context.Configuration
            .GetSection("SeeingAgent:Gateway")
            .Get<GatewayOptions>() ?? new GatewayOptions();

        _host = new GatewayHost(context.Services, gatewayOptions, loggerFactory.CreateLogger<GatewayHost>());
        await _host.StartAsync();

        _logger.LogInformation(
            "Gateway 已启动: http://{BindAddress}:{Port}",
            gatewayOptions.BindAddress,
            gatewayOptions.Port);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _logger?.LogInformation("停止 {Name}", Name);

        if (_host != null)
        {
            await _host.StopAsync();
            _host = null;
        }
    }
}
