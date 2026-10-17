using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using System.Net;
using System.Text;

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
        var input = BuildChatInput(request);
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

    private static ChatInput BuildChatInput(GatewayRequest request)
    {
        if (request.Quote?.Content is { Count: > 0 })
            return BuildChatInputWithQuote(request);

        string? text = null;
        List<ChatAttachment>? attachments = null;

        if (request.Input is { Count: > 0 })
        {
            var textParts = request.Input
                .OfType<GatewayTextContentPart>()
                .Select(p => p.Text)
                .ToList();

            if (textParts.Count > 0)
                text = string.Join("\n", textParts);

            attachments = CollectAttachments(request.Input);
        }

        return new ChatInput
        {
            Text = text,
            Attachments = attachments
        };
    }

    private static ChatInput BuildChatInputWithQuote(GatewayRequest request)
    {
        var quote = request.Quote!;
        var quoteText = ExtractText(quote.Content);
        var userText = ExtractText(request.Input);
        var type = WebUtility.HtmlEncode(quote.MsgType ?? "text");
        var source = string.IsNullOrWhiteSpace(quote.SourceChannel)
            ? string.Empty
            : $" source=\"{WebUtility.HtmlEncode(quote.SourceChannel)}\"";
        var text = new StringBuilder()
            .Append($"<quoted_message type=\"{type}\"{source}>\n")
            .Append(WebUtility.HtmlEncode(quoteText))
            .Append("</quoted_message>\n\n<user_message>\n")
            .Append(WebUtility.HtmlEncode(userText))
            .Append("</user_message>")
            .ToString();

        var attachments = new List<ChatAttachment>();
        if (quote.Content != null)
            attachments.AddRange(CollectAttachments(quote.Content) ?? []);
        if (request.Input != null)
            attachments.AddRange(CollectAttachments(request.Input) ?? []);

        return new ChatInput
        {
            Text = text,
            Attachments = attachments.Count == 0 ? null : attachments
        };
    }

    private static string ExtractText(IReadOnlyList<GatewayContentPart>? parts)
    {
        if (parts == null || parts.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is GatewayTextContentPart text && !string.IsNullOrWhiteSpace(text.Text))
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(text.Text.Trim());
            }
        }

        return builder.ToString();
    }

    private static List<ChatAttachment>? CollectAttachments(IReadOnlyList<GatewayContentPart> parts)
    {
        var attachments = new List<ChatAttachment>();
        foreach (var part in parts)
        {
            if (part is GatewayImageContentPart img)
            {
                attachments.Add(new ChatAttachment
                {
                    Base64Data = img.Url,
                    MimeType = img.MimeType ?? "image/png",
                    FileName = ""
                });
            }
            else if (part is GatewayFileContentPart file)
            {
                attachments.Add(new ChatAttachment
                {
                    Base64Data = file.Url,
                    MimeType = file.MimeType ?? "application/octet-stream",
                    FileName = file.Name ?? ""
                });
            }
        }

        return attachments.Count == 0 ? null : attachments;
    }
}
