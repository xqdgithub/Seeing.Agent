using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Gateway Client 监督器：UI 状态刷新 + 进程管理（委托给 <see cref="ChannelHostManager"/>）。
/// </summary>
public sealed class GatewayClientSupervisor
{
    private readonly GatewayClientConfigService _configService;
    private readonly ChannelHostManager _channelHostManager;
    private readonly ChannelHostConfigStore _channelHostConfigStore;
    private readonly ILogger<GatewayClientSupervisor> _logger;

    public GatewayClientSupervisor(
        GatewayClientConfigService configService,
        ChannelHostManager channelHostManager,
        ChannelHostConfigStore channelHostConfigStore,
        ILogger<GatewayClientSupervisor> logger)
    {
        _configService = configService;
        _channelHostManager = channelHostManager;
        _channelHostConfigStore = channelHostConfigStore;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GatewayClientViewModel>> RefreshStatusesAsync(CancellationToken ct = default)
    {
        var clients = (await _configService.GetClientsAsync(ct)).ToList();
        foreach (var client in clients)
        {
            await RefreshStatusAsync(client, ct);
        }

        return clients;
    }

    public async Task RefreshStatusAsync(GatewayClientViewModel client, CancellationToken ct = default)
    {
        var state = await _channelHostConfigStore.LoadRuntimeStateAsync(client.ChannelId, ct);

        if (!client.Enabled)
        {
            client.Status = GatewayClientStatuses.Disabled;
            client.ProcessId = null;
            client.LastError = state.LastError;
            return;
        }

        if (state.ProcessId is int pid && ChannelHostManager.IsProcessAlive(pid))
        {
            client.ProcessId = pid;

            var connected = await ChannelHostManager.IsChannelConnectedAsync(
                client.ChannelId, client.Gateway.BaseUrl, ct);
            if (connected)
            {
                client.Status = GatewayClientStatuses.Running;
                client.LastError = null;
            }
            else
            {
                client.Status = GatewayClientStatuses.Disconnected;
                client.LastError = "进程已启动但未连接到 Gateway Server";
            }

            return;
        }

        if (state.Status == GatewayClientStatuses.Starting)
        {
            client.Status = string.IsNullOrWhiteSpace(state.LastError)
                ? GatewayClientStatuses.Stopped
                : GatewayClientStatuses.Error;
            client.ProcessId = null;
            client.LastError = state.LastError;
            return;
        }

        client.Status = string.IsNullOrWhiteSpace(state.LastError)
            ? GatewayClientStatuses.Stopped
            : GatewayClientStatuses.Error;
        client.ProcessId = null;
        client.LastError = state.LastError;
    }

    public async Task StartAsync(string channelId, CancellationToken ct = default)
        => await _channelHostManager.StartAsync(channelId, ct);

    public async Task StopAsync(string channelId, CancellationToken ct = default)
        => await _channelHostManager.StopAsync(channelId, ct);

    public async Task RestartAsync(string channelId, CancellationToken ct = default)
        => await _channelHostManager.RestartAsync(channelId, ct);
}
