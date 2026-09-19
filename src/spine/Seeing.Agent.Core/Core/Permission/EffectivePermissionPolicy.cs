using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Configuration;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 生效开关解析（覆盖 &gt; 会话三态 &gt; 全局），被 public <see cref="PermissionService"/> 构造注入。
/// </summary>
public sealed class EffectivePermissionPolicy
{
    private readonly ISessionManager _sessions;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly ISessionGroupManager? _groups;

    /// <summary>创建生效策略解析器。</summary>
    public EffectivePermissionPolicy(
        ISessionManager sessions,
        IOptionsMonitor<SeeingAgentOptions> options,
        ISessionGroupManager? groups = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _groups = groups;
    }

    /// <summary>
    /// 解析生效开关：<c>Allow</c> = 开关级放行；<c>Ask</c> = 强制交互（会话/覆盖 Disabled，短路宿主通道自动批准）；
    /// <c>null</c> = 无开关级判定（继续按 Ask 处理）。<c>RequireInteraction=true</c> 时恒返回 <c>null</c>。
    /// <para>不返回 <c>Deny</c>（拒绝由规则/边界/记忆等上游环节产生）。</para>
    /// </summary>
    public PermissionEffect? Resolve(PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequireInteraction)
            return null;

        var effective = ResolveEffective(request);

        return effective switch
        {
            SessionAutoApprove.Enabled => PermissionEffect.Allow,
            SessionAutoApprove.Disabled => PermissionEffect.Ask,
            _ => _options.CurrentValue.Permission?.AutoApproveAll == true
                ? PermissionEffect.Allow
                : null
        };
    }

    /// <summary>
    /// 解析生效三态：执行级覆盖（显式）&gt; 会话自身三态；若会话为子会话且自身为 <c>FollowGlobal</c>，
    /// 则沿父链实时上溯到最近的非 <c>FollowGlobal</c> 三态（子代理随父会话切换即时生效）。
    /// </summary>
    private SessionAutoApprove ResolveEffective(PermissionRequest request)
    {
        if (request.Override is { } @override && @override != SessionAutoApprove.FollowGlobal)
            return @override;

        var sessionId = request.SessionId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(sessionId) && visited.Add(sessionId))
        {
            var session = _sessions.Get(sessionId);
            if (session is null)
                break;

            if (session.AutoApprove != SessionAutoApprove.FollowGlobal)
                return session.AutoApprove;

            // 仅子会话随父；根会话（含 Fork/Handoff）到此为止，用自身三态
            if (session.Kind != SessionKind.SubAgent ||
                _groups is null ||
                !_groups.TryGetParent(sessionId, out var parent) ||
                string.IsNullOrEmpty(parent))
            {
                break;
            }

            sessionId = parent;
        }

        return SessionAutoApprove.FollowGlobal;
    }
}
