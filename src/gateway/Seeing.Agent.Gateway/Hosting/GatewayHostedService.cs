using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Gateway.Configuration;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 随宿主生命周期启动和停止 Gateway。
/// 调用方应在 <c>app.Run()</c> 之前完成 <c>InitializeSeeingAsync</c>。
/// <para>
/// <b>必须同时受模块结算门控：</b>本服务由 <c>AddHostedService</c> 无条件注册（宿主级基础设施），
/// 若只看 <see cref="GatewayOptions.Enabled"/>/<c>AutoStart</c>，那么能力集把 <c>gateway</c> 模块禁用时
/// （例如 <c>BuiltInCapabilitySets.SecureDisabled</c> 含 <c>gateway</c>/<c>comms</c>）端口仍会监听，
/// 使「能力集 = 声明式安全边界」失效。该门控与工作区切换共用一个判定出口
/// （<see cref="GatewayAutoStartPolicy"/>）。
/// </para>
/// </summary>
public sealed class GatewayHostedService : IHostedService
{
    private readonly IGatewayServer _gatewayServer;
    private readonly GatewayAutoStartPolicy _policy;
    private readonly ILogger<GatewayHostedService> _logger;

    public GatewayHostedService(
        IGatewayServer gatewayServer,
        GatewayAutoStartPolicy policy,
        ILogger<GatewayHostedService> logger)
    {
        _gatewayServer = gatewayServer;
        _policy = policy;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 模块门控优先：能力集禁用 gateway 时不得监听端口（即便 Options.Enabled=true）。
        if (!_policy.IsModuleEnabled)
        {
            _logger.LogInformation("Gateway 未启动：模块 gateway 未在本进程结算中启用（Boot/能力集已禁用）");
            return;
        }

        if (!_policy.IsAutoStartRequested)
        {
            _logger.LogDebug("Gateway 自动启动已跳过（Enabled/AutoStart 不满足或模块未启用）");
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
