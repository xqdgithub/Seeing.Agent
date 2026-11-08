using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// 工作区切换重载处理器：按新工作区重扫渠道插件。
/// <para><b>为什么必须重扫</b>：注册表除了内置插件，还扫描
/// <c>{工作区}/.seeing/gateway-channels</c> 与用户级插件目录。
/// 切换工作区后若不重扫，<see cref="ChannelHostConfigStore.GetChannelHosts"/> 仍按旧工作区的
/// 插件清单产出条目——新工作区装的渠道不出现，已删除的渠道仍被尝试拉起。</para>
/// <para>时机：跑在 <c>WorkspaceChange</c> 这一轮（改根之后、参与者 <c>OnAppliedAsync</c> 之前），
/// 因此 <c>ChannelHostController.StartAllAsync</c> 看到的一定是新工作区的清单。</para>
/// </summary>
public sealed class GatewayChannelRegistryReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly GatewayChannelRegistry _registry;
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<GatewayChannelRegistryReloadHandler> _logger;

    /// <summary>创建处理器。</summary>
    public GatewayChannelRegistryReloadHandler(
        GatewayChannelRegistry registry,
        IWorkspaceProvider workspace,
        ILogger<GatewayChannelRegistryReloadHandler> logger)
    {
        _registry = registry;
        _workspace = workspace;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override string ComponentId => "gateway.channel-registry";

    /// <inheritdoc/>
    protected override Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var root = change.NewWorkspace;
        if (string.IsNullOrWhiteSpace(root))
        {
            // 回滚路径传 NewWorkspace = 旧根，正常切换必带新根；两者都空说明没有可用根。
            if (!_workspace.IsResolved)
                return Task.CompletedTask;

            root = _workspace.GetProjectRoot();
        }

        try
        {
            _registry.Reload(root);
            _logger.LogInformation("工作区切换：渠道插件注册表已按新工作区重扫: {Root}", root);
        }
        catch (Exception ex)
        {
            // 注册表刷新失败不该阻断切换：后续页面操作会再次触发解析，最坏是渠道列表短暂陈旧。
            _logger.LogError(ex, "工作区切换：渠道插件注册表重扫失败: {Root}", root);
        }

        return Task.CompletedTask;
    }
}
