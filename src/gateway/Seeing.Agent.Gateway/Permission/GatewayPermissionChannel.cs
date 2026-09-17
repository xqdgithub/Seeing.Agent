using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Gateway.Models;

namespace Seeing.Agent.Gateway.Permission;

/// <summary>
/// Gateway 宿主审批通道：宿主级 auto_approve 短路。在途请求的查询/回传由
/// <see cref="IPermissionRequestManager"/> 端点（HTTP/WS）直接负责，通道不持有其依赖。
/// 审批事件已随执行流推送，故 <see cref="PresentAsync"/>/<see cref="DismissAsync"/> 为幂等 no-op。
/// </summary>
public sealed class GatewayPermissionChannel : IPermissionChannel
{
    private readonly IOptions<GatewayOptions> _options;

    public GatewayPermissionChannel(IOptions<GatewayOptions> options)
    {
        _options = options;
    }

    /// <summary>宿主级自动批准：PermissionMode=auto_approve → Allow，否则不短路（实时读取）。</summary>
    public PermissionEffect? TryAutoApprove(PermissionRequest request)
    {
        var mode = _options.Value.PermissionMode;
        return string.Equals(mode, "auto_approve", StringComparison.OrdinalIgnoreCase)
            ? PermissionEffect.Allow
            : null;
    }

    public ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    /// <summary>PermissionRequest 全字段映射为 GatewayPendingPermission（CreatedAt 由 DateTimeOffset.UtcDateTime 转 DateTime）。</summary>
    public static GatewayPendingPermission MapPending(PermissionRequest request) => new()
    {
        PermissionId = request.RequestId ?? string.Empty,
        SessionId = request.SessionId,
        LoopId = request.LoopId,
        PermissionKind = request.PermissionKind,
        Resource = request.Resource,
        Arguments = request.Arguments,
        Message = request.Message,
        RiskLevel = request.RiskLevel,
        CreatedAt = request.CreatedAt.UtcDateTime
    };
}
