using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 随宿主生命周期启动和停止 Gateway。
/// 调用方应在 <c>app.Run()</c> 之前完成 <c>InitializeSeeingAgentAsync</c>。
/// </summary>
public sealed class GatewayHostedService : IHostedService
{
    private readonly IGatewayServer _gatewayServer;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayHostedService> _logger;

    public GatewayHostedService(
        IGatewayServer gatewayServer,
        IOptions<GatewayOptions> options,
        ILogger<GatewayHostedService> logger)
    {
        _gatewayServer = gatewayServer;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.AutoStart)
        {
            _logger.LogDebug(
                "Gateway 自动启动已跳过（Enabled={Enabled}, AutoStart={AutoStart}）",
                _options.Enabled,
                _options.AutoStart);
            return;
        }

        // 由 Host 等待启动完成，避免 ApplicationStarted fire-and-forget 任务与停止流程竞态。
        await _gatewayServer.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_gatewayServer.IsRunning)
            return Task.CompletedTask;

        return _gatewayServer.StopAsync(cancellationToken);
    }
}
