using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Gateway.Configuration;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 随宿主生命周期启动和停止 Gateway。
/// 调用方应在 <c>app.Run()</c> 之前完成 <c>InitializeSeeingAsync</c>。
/// <para>
/// <b>必须同时受模块结算门控：</b>本服务由 <c>AddHostedService</c> 无条件注册（宿主级基础设施），
/// 若只看 <see cref="GatewayOptions.Enabled"/>/<c>AutoStart</c>，那么能力集把 <c>gateway</c> 模块禁用时
/// （例如 <c>BuiltInCapabilitySets.SecureDisabled</c> 含 <c>gateway</c>/<c>comms</c>）端口仍会监听，
/// 使「能力集 = 声明式安全边界」失效。
/// </para>
/// </summary>
public sealed class GatewayHostedService : IHostedService
{
    /// <summary>Gateway 模块 id（与 <c>GatewayModule.Id</c> 一致）。</summary>
    private const string GatewayModuleId = "gateway";

    private readonly IGatewayServer _gatewayServer;
    private readonly GatewayOptions _options;
    private readonly IModuleCatalog? _modules;
    private readonly ILogger<GatewayHostedService> _logger;

    public GatewayHostedService(
        IGatewayServer gatewayServer,
        IOptions<GatewayOptions> options,
        ILogger<GatewayHostedService> logger,
        IModuleCatalog? modules = null)
    {
        _gatewayServer = gatewayServer;
        _options = options.Value;
        _logger = logger;
        _modules = modules;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 模块门控优先：能力集禁用 gateway 时不得监听端口（即便 Options.Enabled=true）。
        if (_modules is not null && !_modules.IsEnabled(GatewayModuleId))
        {
            _logger.LogInformation(
                "Gateway 未启动：模块 {ModuleId} 未在本进程结算中启用（Boot/能力集已禁用）",
                GatewayModuleId);
            return;
        }

        if (!_options.Enabled || !_options.AutoStart)
        {
            _logger.LogDebug(
                "Gateway 自动启动已跳过（Enabled={Enabled}, AutoStart={AutoStart}）",
                _options.Enabled,
                _options.AutoStart);
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
