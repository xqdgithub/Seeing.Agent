using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 权限卡片交互服务（Scoped）：卡片动作 → <see cref="IPermissionRequestManager.TryResolve"/>。
/// <para>
/// 调用前校验：卡片仍为 pending、动作作用域在 <see cref="PermissionCardModel.AllowedScopes"/> 内，
/// 否则 no-op（返回 false，不触碰 Manager）。
/// </para>
/// </summary>
public sealed class PermissionInteractionService
{
    private readonly IPermissionRequestManager _manager;
    private readonly ILogger<PermissionInteractionService>? _logger;

    public PermissionInteractionService(
        IPermissionRequestManager manager,
        ILogger<PermissionInteractionService>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
    }

    /// <summary>本次允许（Once）。</summary>
    public bool AllowOnce(PermissionCardModel card)
        => Apply(card, PermissionEffect.Allow, PermissionGrantScope.Once);

    /// <summary>始终允许（Session）。</summary>
    public bool AllowSession(PermissionCardModel card)
        => Apply(card, PermissionEffect.Allow, PermissionGrantScope.Session);

    /// <summary>允许此目录（SessionDirectory，仅 filesystem.*）。</summary>
    public bool AllowSessionDirectory(PermissionCardModel card)
        => Apply(card, PermissionEffect.Allow, PermissionGrantScope.SessionDirectory);

    /// <summary>本次拒绝（Deny + Once）。</summary>
    public bool DenyOnce(PermissionCardModel card)
        => Apply(card, PermissionEffect.Deny, PermissionGrantScope.Once);

    /// <summary>始终拒绝（Deny + Session）。</summary>
    public bool DenySession(PermissionCardModel card)
        => Apply(card, PermissionEffect.Deny, PermissionGrantScope.Session);

    /// <summary>提交动作（校验 pending + 作用域后回传 Manager）。</summary>
    public bool Apply(PermissionCardModel card, PermissionEffect decision, PermissionGrantScope scope)
    {
        if (card is null || string.IsNullOrEmpty(card.RequestId))
            return false;
        if (!card.IsPending)
            return false;
        if (!card.Allows(scope))
        {
            _logger?.LogDebug(
                "权限动作作用域未允许，忽略 Scope={Scope} RequestId={RequestId}", scope, card.RequestId);
            return false;
        }

        return _manager.TryResolve(
            card.RequestId,
            decision,
            scope,
            PermissionResolvedBy.User,
            reason: null,
            expectedSessionId: string.IsNullOrEmpty(card.SessionId) ? null : card.SessionId);
    }
}
