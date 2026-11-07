using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 工作区切换时停掉 Gateway 与渠道子进程，重载完成后再按新根拉起。
/// <para>
/// WebUI 与 Gateway 同进程：Gateway 的配置与渠道列表都来自当前工作区的 <c>.seeing</c>，
/// 切换后不重启就会继续读旧工作区的配置。
/// </para>
/// </summary>
public sealed class GatewayWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IGatewayServer _server;
    private readonly IChannelHostController _channelHosts;
    private readonly ILogger<GatewayWorkspaceParticipant> _logger;

    /// <summary>创建参与者。</summary>
    public GatewayWorkspaceParticipant(
        IGatewayServer server,
        IChannelHostController channelHosts,
        ILogger<GatewayWorkspaceParticipant> logger)
    {
        _server = server;
        _channelHosts = channelHosts;
        _logger = logger;
    }

    /// <summary>切换前 Gateway 是否在运行（由 <see cref="OnApplyingAsync"/> 记录）。</summary>
    public bool WasEnabled { get; set; }

    /// <inheritdoc/>
    public string ComponentId => "gateway";

    /// <inheritdoc/>
    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        WasEnabled = _server.IsRunning;

        await _channelHosts.StopAllAsync(ct).ConfigureAwait(false);

        if (WasEnabled)
            await _server.StopAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：Gateway 已停止（切换前运行={WasEnabled}）", WasEnabled);
    }

    /// <inheritdoc/>
    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        if (!WasEnabled)
            return;

        await _server.StartAsync(ct).ConfigureAwait(false);
        await _channelHosts.StartAllAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：Gateway 已按新工作区启动: {Root}", context.NewRoot);
    }
}
