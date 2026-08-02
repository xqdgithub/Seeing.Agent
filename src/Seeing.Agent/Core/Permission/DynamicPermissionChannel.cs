using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 动态权限通道 — 支持配置热重载的 AutoApproveAll
/// </summary>
public sealed class DynamicPermissionChannel : IPermissionChannel
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _optionsMonitor;
    private readonly ILogger<DynamicPermissionChannel>? _logger;

    public DynamicPermissionChannel(
        IOptionsMonitor<SeeingAgentOptions> optionsMonitor,
        ILogger<DynamicPermissionChannel>? logger = null)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    private bool AutoApproveAll => _optionsMonitor.CurrentValue.Permission?.AutoApproveAll ?? false;

    public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        if (AutoApproveAll)
        {
            _logger?.LogDebug("Auto-approved: {Kind} {Resource}", request.PermissionKind, request.Resource);
            return Task.FromResult(PermissionChannelResult.Allowed());
        }

        throw new PermissionRequiredException(
            request.Resource ?? "未知资源",
            "未配置权限确认通道。请在配置中设置 Permission:AutoApproveAll=true，或提供 IPermissionChannel 实现。");
    }
}
