using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Hosting;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// 在宿主启动后自动启动已启用的 ChannelHost，宿主停止时优雅关闭所有 ChannelHost。
/// 监听 <see cref="IConfigSectionStore.ConfigChanged"/> 实现热重载：启用/禁用 Channel 时自动启停对应进程。
/// </summary>
public sealed class ChannelHostHostedService : IHostedService, IDisposable
{
    private readonly ChannelHostManager _manager;
    private readonly ChannelHostConfigStore _configStore;
    private readonly IConfigSectionStore _configSectionStore;
    private readonly IGatewayServer _gatewayServer;
    private readonly GatewayOptions _gatewayOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ChannelHostHostedService> _logger;

    public ChannelHostHostedService(
        ChannelHostManager manager,
        ChannelHostConfigStore configStore,
        IConfigSectionStore configSectionStore,
        IGatewayServer gatewayServer,
        IOptions<GatewayOptions> gatewayOptions,
        IHostApplicationLifetime lifetime,
        ILogger<ChannelHostHostedService> logger)
    {
        _manager = manager;
        _configStore = configStore;
        _configSectionStore = configSectionStore;
        _gatewayServer = gatewayServer;
        _gatewayOptions = gatewayOptions.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _configSectionStore.ConfigChanged += OnConfigChanged;

        _lifetime.ApplicationStarted.Register(() =>
        {
            _ = StartEnabledAsync();
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _manager.StopAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChannelHost 停止时出现异常");
        }
    }

    public void Dispose()
    {
        _configSectionStore.ConfigChanged -= OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.ContainsSection("GatewayClients") || e.ContainsSection("Gateway"))
        {
            _ = ReconcileAsync();
        }
    }

    private async Task ReconcileAsync()
    {
        try
        {
            var hosts = _configStore.GetChannelHosts();
            foreach (var entry in hosts)
            {
                var state = await _configStore.LoadRuntimeStateAsync(entry.ChannelId);
                var isRunning = state.ProcessId is int pid && ChannelHostManager.IsProcessAlive(pid);

                if (entry.Enabled && !isRunning)
                {
                    try
                    {
                        await _manager.StartAsync(entry.ChannelId);
                        _logger.LogInformation("热重载：已启动 ChannelHost {ChannelId}", entry.ChannelId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "热重载启动 ChannelHost {ChannelId} 失败", entry.ChannelId);
                    }
                }
                else if (!entry.Enabled && isRunning)
                {
                    try
                    {
                        await _manager.StopAsync(entry.ChannelId);
                        _logger.LogInformation("热重载：已停止 ChannelHost {ChannelId}", entry.ChannelId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "热重载停止 ChannelHost {ChannelId} 失败", entry.ChannelId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChannelHost 热重载协调失败");
        }
    }

    private async Task StartEnabledAsync()
    {
        try
        {
            await WaitForGatewayAsync().ConfigureAwait(false);
            await _manager.StartEnabledAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChannelHost 自动启动失败");
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
            _logger.LogWarning("Gateway 未在超时内启动，仍将尝试启动 ChannelHost");
            return;
        }

        await Task.Delay(300).ConfigureAwait(false);
    }
}
