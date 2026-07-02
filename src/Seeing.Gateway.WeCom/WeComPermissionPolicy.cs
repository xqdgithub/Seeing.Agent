using Microsoft.Extensions.Options;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 混合权限策略：低风险自动批准，高风险弹模板卡片
/// </summary>
public sealed class WeComPermissionPolicy
{
    private readonly WeComOptions _options;

    public WeComPermissionPolicy(IOptions<WeComOptions> options)
    {
        _options = options.Value;
    }

    public bool ShouldPromptUser(GatewayEvent gatewayEvent)
    {
        if (gatewayEvent.Object != GatewayEventObject.Permission)
            return false;

        if (!_options.AutoApproveLowRisk)
            return true;

        var data = gatewayEvent.Data;
        if (data == null)
            return false;

        if (!string.IsNullOrWhiteSpace(data.RiskLevel)
            && _options.PromptRiskLevels.Contains(data.RiskLevel, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(data.PermissionKind)
            && _options.PromptPermissionKinds.Contains(data.PermissionKind, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public bool IsAllowEventKey(string eventKey)
        => _options.AllowEventKeys.Contains(eventKey, StringComparer.OrdinalIgnoreCase);

    public bool IsDenyEventKey(string eventKey)
        => _options.DenyEventKeys.Contains(eventKey, StringComparer.OrdinalIgnoreCase);
}
