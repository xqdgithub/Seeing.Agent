using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Hosting;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// 在宿主启动后自动启动已启用的 ChannelHost，宿主停止时优雅关闭所有 ChannelHost。
/// 配置热重载（启用/禁用 Channel 时自动启停对应进程）由 <see cref="ChannelHostReloadHandler"/> 协调。
/// </summary>
public sealed class ChannelHostHostedService : IHostedService
{
    private readonly ChannelHostManager _manager;
    private readonly ChannelHostConfigStore _configStore;
    private readonly IGatewayServer _gatewayServer;
    private readonly GatewayOptions _gatewayOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ChannelHostHostedService> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();

    public ChannelHostHostedService(
        ChannelHostManager manager,
        ChannelHostConfigStore configStore,
        IGatewayServer gatewayServer,
        IOptions<GatewayOptions> gatewayOptions,
        IHostApplicationLifetime lifetime,
        ILogger<ChannelHostHostedService> logger)
    {
        _manager = manager;
        _configStore = configStore;
        _gatewayServer = gatewayServer;
        _gatewayOptions = gatewayOptions.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 宿主停止信号：中止「等待 Gateway → 启动 ChannelHost」链路，
        // 防止宿主在等待窗口内关闭后，ChannelHost 仍被拉起成孤儿进程
        _lifetime.ApplicationStopping.Register(() => _stoppingCts.Cancel());

        _lifetime.ApplicationStarted.Register(() =>
        {
            _ = StartEnabledAsync();
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts.Cancel();
        try
        {
            // 尽力清理：不受 shutdown token 竞速影响（内部链路一律 CancellationToken.None），
            // 保证宿主关闭时所有 ChannelHost 子进程被杀掉
            await _manager.StopAllAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChannelHost 停止时出现异常");
        }
    }

    internal async Task ReconcileAsync()
    {
        try
        {
            // 宿主停止后不再启动新进程（防止热重载晚到拉起孤儿）
            if (_stoppingCts.IsCancellationRequested)
                return;

            if (!_gatewayServer.IsRunning)
            {
                _logger.LogDebug("Gateway 尚未运行，跳过 ChannelHost 热重载协调");
                return;
            }

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
            var ct = _stoppingCts.Token;
            if (!await WaitForGatewayAsync(ct).ConfigureAwait(false))
                return;

            await _manager.StartEnabledAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 宿主在启动等待窗口内停止：放弃启动，StopAsync 已负责清理
            _logger.LogInformation("宿主停止，取消 ChannelHost 自动启动");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChannelHost 自动启动失败");
        }
    }

    private async Task<bool> WaitForGatewayAsync(CancellationToken ct)
    {
        if (!_gatewayOptions.Enabled)
        {
            _logger.LogInformation("Gateway 未启用，跳过 ChannelHost 自动启动");
            return false;
        }

        var deadline = DateTime.Now.AddSeconds(30);
        while (!_gatewayServer.IsRunning && DateTime.Now < deadline)
            await Task.Delay(200, ct).ConfigureAwait(false);

        if (!_gatewayServer.IsRunning)
        {
            _logger.LogWarning("Gateway 未在超时内启动，跳过 ChannelHost 自动启动");
            return false;
        }

        await Task.Delay(300, ct).ConfigureAwait(false);
        return true;
    }
}
