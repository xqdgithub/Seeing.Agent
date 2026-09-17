using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Configuration;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 在途审批唯一权威：登记/等待/幂等完成/清理，审批请求与结果事件的唯一发布点。
/// </summary>
public sealed class PermissionRequestManager : IPermissionRequestManager
{
    private const int MaxPending = 32;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IPermissionChannel> _channels;
    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly IPermissionPresenceStore _presence;
    private readonly EffectivePermissionPolicy _policy;
    private readonly ILogger<PermissionRequestManager> _logger;
    private readonly TimeSpan _timeout;
    private readonly IDisposable? _sessionSubscription;
    private readonly IDisposable? _optionsSubscription;
    private bool _lastAutoApproveAll;
    private bool _disposed;

    /// <summary>创建在途管理器（等待超时 5 分钟）。</summary>
    public PermissionRequestManager(
        IExecutionEventPublisher eventPublisher,
        IPermissionPresenceStore presence,
        EffectivePermissionPolicy policy,
        IEnumerable<IPermissionChannel> channels,
        ISessionEventPublisher sessionEvents,
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<PermissionRequestManager> logger)
        : this(eventPublisher, presence, policy, channels, sessionEvents, options, logger, DefaultTimeout)
    {
    }

    /// <summary>创建在途管理器（可指定等待超时；仅供测试注入短超时）。</summary>
    internal PermissionRequestManager(
        IExecutionEventPublisher eventPublisher,
        IPermissionPresenceStore presence,
        EffectivePermissionPolicy policy,
        IEnumerable<IPermissionChannel> channels,
        ISessionEventPublisher sessionEvents,
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<PermissionRequestManager> logger,
        TimeSpan timeout)
    {
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _presence = presence ?? throw new ArgumentNullException(nameof(presence));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _channels = (channels ?? Array.Empty<IPermissionChannel>()).ToList();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout;

        _lastAutoApproveAll = options?.CurrentValue.Permission?.AutoApproveAll == true;
        _sessionSubscription = sessionEvents?.Events.Subscribe(OnSessionEvent);
        _optionsSubscription = options?.OnChange(OnOptionsChanged);
    }

    /// <inheritdoc />
    public Task<PermissionTicket> BeginAsync(PermissionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestId = string.IsNullOrEmpty(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId!;
        var normalized = request with { RequestId = requestId };

        var entry = new PendingEntry(
            normalized,
            new TaskCompletionSource<PermissionResolution>(TaskCreationOptions.RunContinuationsAsynchronously));

        if (PendingCount >= MaxPending)
        {
            _logger.LogWarning("权限在途队列已满（{Count}），立即拒绝 RequestId={RequestId}", PendingCount, requestId);
            var overflow = new PermissionResolution
            {
                RequestId = requestId,
                SessionId = normalized.SessionId,
                CallId = normalized.CallId,
                Decision = PermissionEffect.Deny,
                Scope = PermissionGrantScope.Once,
                ResolvedBy = PermissionResolvedBy.NoChannel,
                Reason = "队列已满"
            };
            entry.Completion.TrySetResult(overflow);
            _pending[requestId] = entry;
            // 补发终态事件，使溢出拒绝可被 UI/Gateway 观测（事件唯一发布点语义）。
            PublishResolvedEvent(normalized, overflow);
            return Task.FromResult(new PermissionTicket(requestId, normalized.SessionId));
        }

        _pending[requestId] = entry;

        if (!string.IsNullOrEmpty(normalized.SessionId) && !_presence.CanPresent(normalized.SessionId))
            _logger.LogDebug("会话无可交互呈现端，仍登记在途请求 RequestId={RequestId}", requestId);

        PublishRequestEvent(normalized);
        return Task.FromResult(new PermissionTicket(requestId, normalized.SessionId));
    }

    /// <inheritdoc />
    public async Task<PermissionResolution> WaitAsync(PermissionTicket ticket, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(ticket.RequestId, out var entry))
            return Deny(ticket.RequestId, ticket.SessionId, PermissionResolvedBy.NoChannel, "请求不存在或已完成");

        using var registration = ct.Register(() =>
            TryResolve(ticket.RequestId, PermissionEffect.Deny, PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation, "已取消", ticket.SessionId));

        var completed = await Task.WhenAny(entry.Completion.Task, Task.Delay(_timeout, CancellationToken.None))
            .ConfigureAwait(false);

        if (completed != entry.Completion.Task)
        {
            TryResolve(ticket.RequestId, PermissionEffect.Deny, PermissionGrantScope.Once,
                PermissionResolvedBy.Timeout, "审批超时", ticket.SessionId);
        }

        var resolution = await entry.Completion.Task.ConfigureAwait(false);
        _pending.TryRemove(new KeyValuePair<string, PendingEntry>(ticket.RequestId, entry));
        return resolution;
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
        if (string.IsNullOrEmpty(requestId) || !_pending.TryGetValue(requestId, out var entry))
            return false;

        if (!string.IsNullOrEmpty(expectedSessionId) &&
            !string.Equals(entry.Request.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        var resolution = new PermissionResolution
        {
            RequestId = requestId,
            SessionId = entry.Request.SessionId,
            CallId = entry.Request.CallId,
            Decision = decision,
            Scope = scope,
            ResolvedBy = resolvedBy,
            Reason = reason
        };

        if (!entry.Completion.TrySetResult(resolution))
            return false;

        PublishResolvedEvent(entry.Request, resolution);
        _ = BroadcastDismissAsync(resolution);
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<PermissionRequest> GetPending(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<PermissionRequest>();

        return _pending.Values
            .Where(entry => !entry.Completion.Task.IsCompleted)
            .Where(entry => string.Equals(entry.Request.SessionId, sessionId, StringComparison.Ordinal))
            .Select(entry => entry.Request)
            .ToList();
    }

    /// <inheritdoc />
    public int PendingCount => _pending.Values.Count(entry => !entry.Completion.Task.IsCompleted);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _sessionSubscription?.Dispose();
        _optionsSubscription?.Dispose();

        foreach (var requestId in _pending.Keys)
        {
            // 与溢出路径一致：统一经 TryResolve 发布 PermissionResolvedEvent 并广播 DismissAsync。
            TryResolve(requestId, PermissionEffect.Deny, PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation, "管理器已释放");
        }

        _pending.Clear();
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

    private void OnSessionEvent(SessionEvent sessionEvent)
    {
        try
        {
            if (sessionEvent?.Type != SessionEventType.Updated || string.IsNullOrEmpty(sessionEvent.SessionId))
                return;

            ReEvaluate(sessionEvent.SessionId);
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

            foreach (var sessionId in _pending.Values
                         .Where(entry => !entry.Completion.Task.IsCompleted)
                         .Select(entry => entry.Request.SessionId)
                         .Where(id => !string.IsNullOrEmpty(id))
                         .Distinct(StringComparer.Ordinal))
            {
                ReEvaluate(sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全局配置变更触发权限重评估失败");
        }
    }

    private void ReEvaluate(string sessionId)
    {
        if (_disposed)
            return;

        foreach (var entry in GetPending(sessionId))
        {
            if (_policy.Resolve(entry) != PermissionEffect.Allow)
                continue;

            TryResolve(entry.RequestId!, PermissionEffect.Allow, PermissionGrantScope.Once,
                PermissionResolvedBy.Policy, "策略变更放行", sessionId);
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

    private sealed record PendingEntry(
        PermissionRequest Request,
        TaskCompletionSource<PermissionResolution> Completion);
}
