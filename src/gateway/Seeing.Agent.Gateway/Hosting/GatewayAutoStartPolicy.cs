using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Gateway.Configuration;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 「Gateway 此刻应当自动运行吗」的唯一判定出口。
/// <para><b>为什么必须收成一处</b>：宿主启动（<see cref="GatewayHostedService"/>）与工作区切换
/// （<see cref="GatewayWorkspaceParticipant"/>）问的是同一个问题。分散两处写过一次就出过事——
/// 切换那侧看的是「切换前是否在运行」，于是「未选工作区启动 → Gateway 从未运行 → 选定工作区后
/// 即使配置写着 Enabled + AutoStart 也不启动」。</para>
/// <para>三条约束按优先级：<b>模块结算</b>（能力集禁用 gateway 时不得监听端口，即便 Enabled=true）、
/// <b>Enabled</b>（用户显式关闭）、<b>AutoStart</b>（是否随宿主/工作区自动拉起）。</para>
/// </summary>
public sealed class GatewayAutoStartPolicy
{
    private const string GatewayModuleId = "gateway";

    private readonly IOptions<GatewayOptions> _options;
    private readonly IModuleCatalog? _modules;

    /// <summary>创建判定器。</summary>
    public GatewayAutoStartPolicy(
        IOptions<GatewayOptions> options,
        IModuleCatalog? modules = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _modules = modules;
    }

    /// <summary>模块是否在本进程结算中启用（无模块目录时视为启用，兼容未接入结算的宿主）。</summary>
    public bool IsModuleEnabled => _modules is null || _modules.IsEnabled(GatewayModuleId);

    /// <summary>配置是否允许运行 Gateway（不看 AutoStart）。</summary>
    public bool IsEnabled => IsModuleEnabled && _options.Value.Enabled;

    /// <summary>是否应当自动启动：模块启用 + Enabled + AutoStart。</summary>
    public bool IsAutoStartRequested => IsEnabled && _options.Value.AutoStart;
}
