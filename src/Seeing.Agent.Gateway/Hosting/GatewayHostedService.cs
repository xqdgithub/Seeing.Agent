using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 在宿主 <see cref="IHostApplicationLifetime.ApplicationStarted"/> 后自动启动 Gateway。
/// 调用方应在 <c>app.Run()</c> 之前完成 <c>InitializeSeeingAgentAsync</c>。
/// </summary>
public sealed class GatewayHostedService : IHostedService
{
    private readonly IGatewayServer _gatewayServer;
    private readonly GatewayOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GatewayHostedService> _logger;

    public GatewayHostedService(
        IGatewayServer gatewayServer,
        IOptions<GatewayOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<GatewayHostedService> logger)
    {
        _gatewayServer = gatewayServer;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.AutoStart)
        {
            _logger.LogDebug(
                "Gateway 自动启动已跳过（Enabled={Enabled}, AutoStart={AutoStart}）",
                _options.Enabled,
                _options.AutoStart);
            return Task.CompletedTask;
        }

        _lifetime.ApplicationStarted.Register(() =>
        {
            _ = StartGatewayAsync();
        });

        _lifetime.ApplicationStopping.Register(() =>
        {
            _ = StopGatewayAsync();
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
        => _gatewayServer.StopAsync(cancellationToken);

    private async Task StartGatewayAsync()
    {
        try
        {
            await _gatewayServer.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway 自动启动失败");
        }
    }

    private async Task StopGatewayAsync()
    {
        try
        {
            await _gatewayServer.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway 停止时出现异常");
        }
    }
}
