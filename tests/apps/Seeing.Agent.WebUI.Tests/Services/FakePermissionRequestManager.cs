using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>测试替身：在途请求管理器（GetAllPending 可对账；Seed/Remove 触发 PendingChanged）。</summary>
internal sealed class FakePermissionRequestManager : IPermissionRequestManager
{
    private readonly Dictionary<string, PermissionRequest> _pending = new(StringComparer.Ordinal);

    public List<ResolveCall> ResolveCalls { get; } = new();

    public bool ResolveResult { get; set; } = true;

    public event Action? PendingChanged;

    public void Seed(params PermissionRequest[] requests)
    {
        foreach (var request in requests)
        {
            if (!string.IsNullOrEmpty(request.RequestId))
                _pending[request.RequestId!] = request;
        }
        PendingChanged?.Invoke();
    }

    public bool Remove(string requestId)
    {
        var removed = _pending.Remove(requestId);
        if (removed)
            PendingChanged?.Invoke();
        return removed;
    }

    public Task<RequestTicket> BeginAsync(PermissionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<PermissionResolution> WaitAsync(RequestTicket ticket, CancellationToken ct = default)
        => throw new NotSupportedException();

    public bool TryResolve(string requestId, PermissionResolution response, string? expectedSessionId = null)
        => throw new NotSupportedException();

    public bool TryResolve(
        string requestId,
        PermissionEffect decision,
        PermissionGrantScope scope,
        PermissionResolvedBy resolvedBy,
        string? reason = null,
        string? expectedSessionId = null)
    {
        ResolveCalls.Add(new ResolveCall(requestId, decision, scope, resolvedBy, reason, expectedSessionId));
        if (!ResolveResult)
            return false;
        if (_pending.Remove(requestId))
            PendingChanged?.Invoke();
        return true;
    }

    public IReadOnlyList<PermissionRequest> GetPending(string sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? Array.Empty<PermissionRequest>()
            : _pending.Values
                .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();

    public IReadOnlyList<PermissionRequest> GetAllPending() => _pending.Values.ToList();

    public int PendingCount => _pending.Count;

    public void Dispose()
    {
    }

    public sealed record ResolveCall(
        string RequestId,
        PermissionEffect Decision,
        PermissionGrantScope Scope,
        PermissionResolvedBy ResolvedBy,
        string? Reason,
        string? ExpectedSessionId);
}
