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
/// <para>
/// <b>「切换后是否要运行」由新工作区的配置决定，不看切换前的运行状态。</b>
/// 旧实现只看 <c>WasRunning</c>，于是最常见的首次路径完全失效：未选工作区启动 WebUI →
/// <c>Gateway</c> 是 ProjectOnly 节、读不到 → Gateway 从未运行 → 用户在工作区首页选定目录后
/// 即使新工作区写着 <c>Enabled=true, AutoStart=true</c> 也不会启动，只能去页面手动关一次再开一次。
/// </para>
/// </summary>
public sealed class GatewayWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IGatewayServer _server;
    private readonly IChannelHostController _channelHosts;
    private readonly GatewayAutoStartPolicy _policy;
    private readonly ILogger<GatewayWorkspaceParticipant> _logger;

    /// <summary>创建参与者。</summary>
    public GatewayWorkspaceParticipant(
        IGatewayServer server,
        IChannelHostController channelHosts,
        GatewayAutoStartPolicy policy,
        ILogger<GatewayWorkspaceParticipant> logger)
    {
        _server = server;
        _channelHosts = channelHosts;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>切换前 Gateway 是否在运行（由 <see cref="OnApplyingAsync"/> 记录）。</summary>
    public bool WasRunning { get; set; }

    /// <inheritdoc/>
    public string ComponentId => "gateway";

    /// <inheritdoc/>
    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        WasRunning = _server.IsRunning;

        await _channelHosts.StopAllAsync(ct).ConfigureAwait(false);

        if (WasRunning)
            await _server.StopAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：Gateway 已停止（切换前运行={WasRunning}）", WasRunning);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>判定</b>：切换前在运行 → 只要新工作区仍允许（<c>Enabled</c>）就继续运行，
    /// 保住「用户手动开的、AutoStart=false」这种状态；切换前没运行 → 只有新工作区要求自动启动
    /// （<c>Enabled</c> + <c>AutoStart</c>）才拉起。</para>
    /// <para>此刻配置已随工作区重载完毕（切换流程在参与者回填之前跑过 WorkspaceChange + ConfigChange
    /// 两轮），所以 <see cref="GatewayAutoStartPolicy"/> 读到的是新工作区的值。</para>
    /// </remarks>
    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        var shouldRun = WasRunning ? _policy.IsEnabled : _policy.IsAutoStartRequested;
        if (!shouldRun)
        {
            _logger.LogInformation(
                "工作区切换：新工作区不要求 Gateway 运行（Enabled={Enabled}, AutoStart={AutoStart}, 切换前运行={WasRunning}）: {Root}",
                _policy.IsEnabled,
                _policy.IsAutoStartRequested,
                WasRunning,
                context.NewRoot);
            return;
        }

        await _server.StartAsync(ct).ConfigureAwait(false);
        await _channelHosts.StartAllAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：Gateway 已按新工作区启动: {Root}", context.NewRoot);
    }
}
