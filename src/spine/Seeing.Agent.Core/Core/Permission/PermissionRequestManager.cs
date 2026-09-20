using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Interactions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 在途审批唯一权威：通用在途机制由 <see cref="PendingRequestManager{TRequest,TResponse}"/> 提供，
/// 本类只负责权限领域语义（溢出拒绝、事件发布、Dismiss 广播、重评估、呈现端注销收敛）。
/// </summary>
public sealed class PermissionRequestManager : PendingRequestManager<PermissionRequest, PermissionResolution>, IPermissionRequestManager
{
    private const int MaxPending = 32;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<IPermissionChannel> _channels;
    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly IPermissionSurfaceRegistry _presentation;
    private readonly EffectivePermissionPolicy _policy;
    private readonly ILogger<PermissionRequestManager> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _convergenceGrace;
    private readonly Timer _convergenceTimer;
    private readonly IDisposable? _sessionSubscription;
    private readonly IDisposable? _optionsSubscription;
    private readonly object _convergenceGate = new();
    private HashSet<string>? _convergenceCandidates;
    private bool _convergenceArmed;
    private bool _lastAutoApproveAll;
    private bool _disposed;

    /// <summary>创建在途管理器（等待超时 5 分钟；收敛宽限 1 秒）。</summary>
    public PermissionRequestManager(
        IExecutionEventPublisher eventPublisher,
        IPermissionSurfaceRegistry presentation,
        EffectivePermissionPolicy policy,
        IEnumerable<IPermissionChannel> channels,
        ISessionEventPublisher sessionEvents,
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<PermissionRequestManager> logger)
        : this(eventPublisher, presentation, policy, channels, sessionEvents, options, logger, DefaultTimeout)
    {
    }

    /// <summary>创建在途管理器（可指定等待超时与收敛宽限；仅供测试注入短值）。</summary>
    internal PermissionRequestManager(
        IExecutionEventPublisher eventPublisher,
        IPermissionSurfaceRegistry presentation,
        EffectivePermissionPolicy policy,
        IEnumerable<IPermissionChannel> channels,
        ISessionEventPublisher sessionEvents,
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<PermissionRequestManager> logger,
        TimeSpan timeout,
        TimeSpan? convergenceGrace = null)
        : base(logger)
    {
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _channels = (channels ?? Array.Empty<IPermissionChannel>()).ToList();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout;
        _convergenceGrace = convergenceGrace ?? TimeSpan.FromSeconds(1);

        _lastAutoApproveAll = options?.CurrentValue.Permission?.AutoApproveAll == true;
        _sessionSubscription = sessionEvents?.Events.Subscribe(OnSessionEvent);
        _optionsSubscription = options?.OnChange(OnOptionsChanged);

        _presentation.ProviderUnregistered += OnProviderUnregistered;
        _convergenceTimer = new Timer(OnConvergenceTimer, null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    protected override TimeSpan Timeout => _timeout;

    /// <inheritdoc />
    protected override string GetRequestId(PermissionRequest request) => request.RequestId ?? string.Empty;

    /// <inheritdoc />
    protected override PermissionRequest AssignRequestId(PermissionRequest request, string id) => request with { RequestId = id };

    /// <inheritdoc />
    protected override string GetSessionId(PermissionRequest request) => request.SessionId;

    /// <inheritdoc />
    protected override PermissionResolution CreateFallback(PermissionRequest request, PendingFallbackReason reason) => reason switch
    {
        PendingFallbackReason.Timeout => Deny(request.RequestId!, request.SessionId, PermissionResolvedBy.Timeout, "审批超时"),
        PendingFallbackReason.Cancelled => Deny(request.RequestId!, request.SessionId, PermissionResolvedBy.Cancellation, "已取消"),
        PendingFallbackReason.Disposed => Deny(request.RequestId!, request.SessionId, PermissionResolvedBy.Cancellation, "管理器已释放"),
        _ => Deny(request.RequestId!, request.SessionId, PermissionResolvedBy.NoChannel, "呈现端已移除")
    };

    /// <inheritdoc />
    protected override PermissionResolution CreateMissingResponse(RequestTicket ticket) =>
        Deny(ticket.RequestId, ticket.SessionId, PermissionResolvedBy.NoChannel, "请求不存在或已完成");

    /// <inheritdoc />
    protected override PermissionResolution? ValidateOnBegin(PermissionRequest request)
    {
        if (PendingCount < MaxPending)
            return null;

        _logger.LogWarning("权限在途队列已满（{Count}），立即拒绝 RequestId={RequestId}", PendingCount, request.RequestId);
        return Deny(request.RequestId!, request.SessionId, PermissionResolvedBy.NoChannel, "队列已满");
    }

    /// <inheritdoc />
    protected override void OnRequestBegan(PermissionRequest request) => PublishRequestEvent(request);

    /// <inheritdoc />
    protected override void OnResolved(PermissionRequest request, PermissionResolution resolution)
    {
        PublishResolvedEvent(request, resolution);
        _ = BroadcastDismissAsync(resolution);
    }

    /// <inheritdoc />
    protected override PermissionResolution FinalizeResponse(PermissionRequest request, PermissionResolution response) =>
        response with { SessionId = request.SessionId, CallId = request.CallId };

    /// <inheritdoc />
    protected override void OnDisposing()
    {
        _disposed = true;

        _presentation.ProviderUnregistered -= OnProviderUnregistered;
        _convergenceTimer.Dispose();
        _sessionSubscription?.Dispose();
        _optionsSubscription?.Dispose();
    }

    /// <inheritdoc />
    public bool TryResolve(
        string requestId,
        PermissionEffect decision,
        PermissionGrantScope scope,
        PermissionResolvedBy resolvedBy,
        string? reason = null,
        string? expectedSessionId = null)
    {
        if (string.IsNullOrEmpty(requestId))
            return false;

        // SessionId/CallId 由 FinalizeResponse 从在途请求补全；此处只提供权限决议字段。
        var resolution = new PermissionResolution
        {
            RequestId = requestId,
            SessionId = expectedSessionId ?? string.Empty,
            Decision = decision,
            Scope = scope,
            ResolvedBy = resolvedBy,
            Reason = reason
        };

        return TryResolve(requestId, resolution, expectedSessionId);
    }

    private static PermissionResolution Deny(
        string requestId,
        string sessionId,
        PermissionResolvedBy resolvedBy,
        string reason) => new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            Decision = PermissionEffect.Deny,
            Scope = PermissionGrantScope.Once,
            ResolvedBy = resolvedBy,
            Reason = reason
        };

    // 仅在呈现端注销（身份移除）时收敛：注册与 SurfaceSessionIds 内容收缩都不收敛（spec §4.4）。
    private void OnProviderUnregistered()
    {
        if (_disposed)
            return;

        lock (_convergenceGate)
        {
            // 合并注销时刻及此前已登记的在途请求；已在计时中的变化不重置计时器（防高频注销饿死）。
            _convergenceCandidates ??= new HashSet<string>(StringComparer.Ordinal);
            _convergenceCandidates.UnionWith(GetAllPending().Select(entry => entry.RequestId!).Where(id => !string.IsNullOrEmpty(id)));

            if (_convergenceArmed)
                return;

            _convergenceArmed = true;
        }

        try
        {
            _convergenceTimer.Change(_convergenceGrace, System.Threading.Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnConvergenceTimer(object? state)
    {
        if (_disposed)
            return;

        HashSet<string>? candidates;
        lock (_convergenceGate)
        {
            candidates = _convergenceCandidates;
            _convergenceCandidates = null;
            _convergenceArmed = false;
        }

        if (candidates is null)
            return;

        try
        {
            foreach (var requestId in candidates)
            {
                var pending = GetAllPending().FirstOrDefault(entry => string.Equals(entry.RequestId, requestId, StringComparison.Ordinal));
                if (pending is null)
                    continue;

                var sessionId = pending.SessionId;
                if (!string.IsNullOrEmpty(sessionId) && _presentation.CanSurface(sessionId))
                    continue;

                TryResolve(requestId, PermissionEffect.Deny, PermissionGrantScope.Once,
                    PermissionResolvedBy.NoChannel, "呈现端已移除", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "在途权限收敛复核失败");
        }
    }

    private void OnSessionEvent(SessionEvent sessionEvent)
    {
        try
        {
            if (sessionEvent?.Type != SessionEventType.Updated || string.IsNullOrEmpty(sessionEvent.SessionId))
                return;

            // 任一会话变更都可能改变在途请求的生效开关：
            // 父会话切换三态需即时作用于子代理在途请求（EffectivePermissionPolicy 沿父链实时解析）。
            ReEvaluatePending();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话事件触发权限重评估失败 SessionId={SessionId}", sessionEvent?.SessionId);
        }
    }

    private void OnOptionsChanged(SeeingAgentOptions options, string? name)
    {
        try
        {
            var current = options?.Permission?.AutoApproveAll == true;
            if (current == _lastAutoApproveAll)
                return;

            _lastAutoApproveAll = current;
            ReEvaluatePending();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全局配置变更触发权限重评估失败");
        }
    }

    private void ReEvaluatePending()
    {
        if (_disposed || PendingCount == 0)
            return;

        foreach (var entry in GetAllPending())
        {
            if (_policy.Resolve(entry) != PermissionEffect.Allow)
                continue;

            TryResolve(entry.RequestId!, PermissionEffect.Allow, PermissionGrantScope.Once,
                PermissionResolvedBy.Policy, "策略变更放行", entry.SessionId);
        }
    }

    private void PublishRequestEvent(PermissionRequest request)
    {
        try
        {
            _eventPublisher.Publish(request.SessionId, new PermissionRequestEvent
            {
                SessionId = request.SessionId,
                LoopId = request.LoopId,
                RequestId = request.RequestId!,
                CallId = request.CallId,
                PermissionKind = request.PermissionKind,
                Resource = request.Resource,
                Arguments = request.Arguments,
                RiskLevel = request.RiskLevel,
                Message = request.Message,
                AllowedScopes = request.AllowedScopes,
                TimeoutSeconds = (int)_timeout.TotalSeconds,
                Timestamp = request.CreatedAt.LocalDateTime
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发布权限请求事件失败 RequestId={RequestId}", request.RequestId);
        }
    }

    private void PublishResolvedEvent(PermissionRequest request, PermissionResolution resolution)
    {
        try
        {
            _eventPublisher.Publish(request.SessionId, new PermissionResolvedEvent
            {
                SessionId = request.SessionId,
                LoopId = request.LoopId,
                RequestId = resolution.RequestId,
                CallId = resolution.CallId,
                Decision = resolution.Decision,
                Scope = resolution.Scope,
                ResolvedBy = resolution.ResolvedBy,
                Reason = resolution.Reason,
                Timestamp = resolution.ResolvedAt.LocalDateTime
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发布权限结果事件失败 RequestId={RequestId}", resolution.RequestId);
        }
    }

    private async Task BroadcastDismissAsync(PermissionResolution resolution)
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.DismissAsync(resolution).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "广播权限结果失败 Channel={Channel}", channel.GetType().Name);
            }
        }
    }
}
