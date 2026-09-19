using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;

namespace Seeing.Agent.Acp.Tests;

internal sealed class CapturingPermissionAuthorizerFactory : IPermissionAuthorizerFactory
{
    public PermissionEffect Decision { get; set; } = PermissionEffect.Allow;

    public string? Reason { get; set; }

    public string? CapturedSessionId { get; private set; }

    public PermissionRequest? LastRequest { get; private set; }

    public IPermissionAuthorizer Create(string sessionId, SessionAutoApprove? @override = null)
    {
        CapturedSessionId = sessionId;
        return new CapturingAuthorizer(this, sessionId);
    }

    private sealed class CapturingAuthorizer : IPermissionAuthorizer
    {
        private readonly CapturingPermissionAuthorizerFactory _owner;

        public CapturingAuthorizer(CapturingPermissionAuthorizerFactory owner, string sessionId)
        {
            _owner = owner;
            SessionId = sessionId;
        }

        public string SessionId { get; }

        public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
        {
            _owner.LastRequest = request;
            return Task.FromResult(new PermissionResolution
            {
                RequestId = request.RequestId ?? "req-test",
                SessionId = string.IsNullOrEmpty(request.SessionId) ? SessionId : request.SessionId,
                CallId = request.CallId,
                Decision = _owner.Decision,
                Reason = _owner.Reason
            });
        }
    }
}

/// <summary>记录最近一次请求并返回预设决策的直接授权器（用于 context.PermissionAuthorizer 注入测试）。</summary>
internal sealed class CapturingPermissionAuthorizer : IPermissionAuthorizer
{
    private readonly PermissionEffect _decision;

    public CapturingPermissionAuthorizer(PermissionEffect decision = PermissionEffect.Allow, string reason = "允许")
    {
        _decision = decision;
        Reason = reason;
        SessionId = "context-session";
    }

    public string Reason { get; }

    public PermissionRequest? LastRequest { get; private set; }

    public string SessionId { get; }

    public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new PermissionResolution
        {
            RequestId = request.RequestId ?? "req-ctx",
            SessionId = string.IsNullOrEmpty(request.SessionId) ? SessionId : request.SessionId,
            CallId = request.CallId,
            Decision = _decision,
            Reason = Reason
        });
    }
}
