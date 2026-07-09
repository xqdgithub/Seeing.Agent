using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Hosting;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 在宿主启动后自动启动已启用的 Gateway Client，并在宿主停止时清理进程。
/// </summary>
public sealed class GatewayClientHostedService : IHostedService
{
    private readonly GatewayClientSupervisor _supervisor;
    private readonly IGatewayServer _gatewayServer;
    private readonly GatewayOptions _gatewayOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GatewayClientHostedService> _logger;

    public GatewayClientHostedService(
        GatewayClientSupervisor supervisor,
        IGatewayServer gatewayServer,
        IOptions<GatewayOptions> gatewayOptions,
        IHostApplicationLifetime lifetime,
        ILogger<GatewayClientHostedService> logger)
    {
        _supervisor = supervisor;
        _gatewayServer = gatewayServer;
        _gatewayOptions = gatewayOptions.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(() =>
        {
            _ = StartEnabledClientsAsync();
        });

        _lifetime.ApplicationStopping.Register(() =>
        {
            _ = StopRunningClientsAsync();
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task StartEnabledClientsAsync()
    {
        try
        {
            await WaitForGatewayAsync().ConfigureAwait(false);
            await _supervisor.StartEnabledClientsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway Client 自动启动失败");
        }
    }

    private async Task StopRunningClientsAsync()
    {
        try
        {
            await _supervisor.StopRunningClientsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway Client 停止时出现异常");
        }
    }

    private async Task WaitForGatewayAsync()
    {
        if (!_gatewayOptions.Enabled)
            return;

        var deadline = DateTime.Now.AddSeconds(30);
        while (!_gatewayServer.IsRunning && DateTime.Now < deadline)
            await Task.Delay(200).ConfigureAwait(false);

        if (!_gatewayServer.IsRunning)
        {
            _logger.LogWarning("Gateway 未在超时内启动，仍将尝试启动 Gateway Client");
            return;
        }

        await Task.Delay(300).ConfigureAwait(false);
    }
}
