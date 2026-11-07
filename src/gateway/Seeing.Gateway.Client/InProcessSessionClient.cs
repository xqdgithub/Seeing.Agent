using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Gateway.Client;

/// <summary>
/// 进程内会话客户端：直接调用 <see cref="ISessionRuntime"/>。
/// </summary>
public sealed class InProcessSessionClient : ISessionClient
{
    private readonly ISessionRuntime _runtime;
    private readonly IServiceProvider _services;
    private readonly SessionEventPump _pump;

    public InProcessSessionClient(
        ISessionRuntime runtime,
        IServiceProvider services,
        GatewayRunTracker runTracker,
        IPermissionSurfaceRegistry presentation,
        ILogger<InProcessSessionClient>? logger = null)
    {
        _runtime = runtime;
        _services = services;
        _pump = new SessionEventPump(runtime, runTracker, presentation, logger);
    }

    public async Task<GatewaySubmitAckPayload> SubmitAsync(
        GatewayRequest request,
        CancellationToken ct = default)
    {
        var input = GatewayContentPartMapper.ToChatInput(request.Input, request.Quote);
        var result = await _runtime.DeliverAsync(
            new InboxMessage
            {
                TargetSessionId = request.SessionId,
                Sender = new InboxSender
                {
                    Kind = InboxSenderKind.Human,
                    Id = request.Sender?.ExternalId ?? "gateway"
                },
                Body = input.Text,
                Delivery = InboxDelivery.Wake,
                Presentation = InboxPresentation.User,
                Attachments = input.Attachments,
                SourceTag = request.ChannelId,
                AgentId = request.AgentId,
                ModelId = request.ModelId,
                Conversation = request.Conversation,
                ReplyToMsgId = request.ReplyToMsgId,
                ThinkingEffort = request.ThinkingEffort,
                ModeId = request.ModeId
            },
            ct).ConfigureAwait(false);

        return new GatewaySubmitAckPayload
        {
            SessionId = request.SessionId,
            Success = result.Success,
            Error = result.Error,
            PendingInputCount = result.PendingInputCount
        };
    }

    public IAsyncEnumerable<GatewayEvent> SubscribeAsync(string sessionId, CancellationToken ct = default) =>
        _pump.StreamAsync(sessionId, ct);

    /// <summary>进程内无共享队列串轮问题，requestId 忽略（等价于无参重载）。</summary>
    public IAsyncEnumerable<GatewayEvent> SubscribeAsync(
        string sessionId, string? requestId, CancellationToken ct = default) =>
        _pump.StreamAsync(sessionId, ct);

    public Task<bool> CancelAsync(string sessionId, CancellationToken ct = default) =>
        _runtime.InterruptAsync(sessionId, ct);

    public Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        bool allow,
        string? reason = null,
        CancellationToken ct = default)
    {
        _ = ct;
        using var scope = _services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPermissionRequestManager>();
        var resolved = manager.TryResolve(
            permissionId,
            allow ? PermissionEffect.Allow : PermissionEffect.Deny,
            PermissionGrantScope.Once,
            PermissionResolvedBy.User,
            reason,
            expectedSessionId: sessionId);

        return Task.FromResult(resolved
            ? GatewayPermissionRespondResult.Ok()
            : GatewayPermissionRespondResult.Fail("权限请求不存在、已过期或会话不匹配"));
    }
}
