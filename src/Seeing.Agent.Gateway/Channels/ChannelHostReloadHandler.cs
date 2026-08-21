using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// Gateway/ChannelHost 配置变更重载处理器。
/// 由统一重载编排器在 Gateway/GatewayClients 配置节变更时调度，触发 ChannelHost 的启用/禁用热重载。
/// </summary>
public sealed class ChannelHostReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly ChannelHostHostedService _service;

    public ChannelHostReloadHandler(ChannelHostHostedService service) => _service = service;

    /// <inheritdoc/>
    public override string ComponentId => "channel-host";

    /// <inheritdoc/>
    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0 ||
            change.ChangedSections.Contains("GatewayClients") ||
            change.ChangedSections.Contains("Gateway"))
        {
            _ = _service.ReconcileAsync();
        }

        return Task.CompletedTask;
    }
}
