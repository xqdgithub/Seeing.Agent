using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>渠道子进程批量启停的默认实现。</summary>
public sealed class ChannelHostController : IChannelHostController
{
    private readonly ChannelHostManager _manager;
    private readonly ChannelHostConfigStore _configStore;
    private readonly ILogger<ChannelHostController> _logger;

    /// <summary>创建控制器。</summary>
    public ChannelHostController(
        ChannelHostManager manager,
        ChannelHostConfigStore configStore,
        ILogger<ChannelHostController> logger)
    {
        _manager = manager;
        _configStore = configStore;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var entry in _configStore.GetChannelHosts())
        {
            try
            {
                var state = await _configStore.LoadRuntimeStateAsync(entry.ChannelId, ct).ConfigureAwait(false);
                if (state.ProcessId is not int pid || !ChannelHostManager.IsProcessAlive(pid))
                    continue;

                await _manager.StopAsync(entry.ChannelId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个渠道停不掉不应阻塞工作区切换，但必须记录
                _logger.LogWarning(ex, "停止渠道子进程失败: {ChannelId}", entry.ChannelId);
            }
        }
    }

    /// <inheritdoc/>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var entry in _configStore.GetChannelHosts())
        {
            if (!entry.Enabled)
                continue;

            try
            {
                await _manager.StartAsync(entry.ChannelId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动渠道子进程失败: {ChannelId}", entry.ChannelId);
            }
        }
    }
}
