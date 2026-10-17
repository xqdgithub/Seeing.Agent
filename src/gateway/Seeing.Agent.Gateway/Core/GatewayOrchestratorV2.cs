using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Gateway.Client;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// Gateway 会话编排器 — Deliver / Subscribe / Cancel（会话级，对齐 ISessionRuntime）。
/// </summary>
public sealed class GatewayOrchestratorV2
{
    private readonly IServiceProvider _services;
    private readonly GatewayOptions _options;
    private readonly GatewayRunTracker _runTracker;
    private readonly ILogger<GatewayOrchestratorV2> _logger;

    public GatewayOrchestratorV2(
        IServiceProvider services,
        GatewayOptions options,
        GatewayRunTracker runTracker,
        ILogger<GatewayOrchestratorV2> logger)
    {
        _services = services;
        _options = options;
        _runTracker = runTracker;
        _logger = logger;
    }

    /// <summary>提交执行（非阻塞）：以 Human 用户消息投递并唤醒会话消费循环。</summary>
    public async Task<GatewaySubmitResult> SubmitAsync(
        GatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var sessionId = request.SessionId;

        using var scope = _services.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ISessionRuntime>();
        var input = BuildChatInput(request);

        var result = await runtime.DeliverAsync(
                new InboxMessage
                {
                    TargetSessionId = sessionId,
                    Sender = new InboxSender { Kind = InboxSenderKind.Human, Id = request.Sender?.ExternalId },
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
                cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            return GatewaySubmitResult.Failed(sessionId, result.Error ?? "Submit failed");

        return GatewaySubmitResult.Succeeded(sessionId, result.PendingInputCount);
    }

    /// <summary>
    /// 会话级订阅：流式转发会话事件，直到消费循环空闲（<see cref="ISessionRuntime.WaitForIdleAsync"/> 语义）
    /// 或取消/超时。不再按 executionId 过滤。含环形缓冲回放（UI/SSE 重连语义）。
    /// </summary>
    public async IAsyncEnumerable<GatewayEvent> SubscribeSessionEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ISessionRuntime>();
        var presentation = _services.GetRequiredService<IPermissionSurfaceRegistry>();
        var pump = new SessionEventPump(runtime, _runTracker, presentation, _logger);
        await foreach (var gatewayEvent in pump.StreamAsync(sessionId, cancellationToken).ConfigureAwait(false))
            yield return gatewayEvent;
    }

    /// <summary>
    /// 请求/应答实时流：先同步注册（呈现端 + run 跟踪 + 实时订阅，不回放），
    /// 供 WS submit 等请求/应答消费方在 <see cref="SubmitAsync"/>（投递唤醒）<b>之前</b>调用，
    /// 保证整轮事件自 LoopStart 起不漏；桥侧配合 <c>GatewayAssistantReplyCollector</c>
    /// 轮次门控忽略上一轮仍在途的尾流。投递触发后枚举 <see cref="LiveSessionEventStream.StreamAsync"/>；
    /// <see cref="LiveSessionEventStream.Dispose"/> 注销全部注册。
    /// </summary>
    public LiveSessionEventStream BeginReplyStream(string sessionId)
    {
        var publisher = _services.GetRequiredService<IExecutionEventPublisher>();
        var runtime = _services.GetRequiredService<ISessionRuntime>();
        var presentation = _services.GetRequiredService<IPermissionSurfaceRegistry>();
        var pump = new SessionEventPump(runtime, _runTracker, presentation, _logger);
        var subscription = publisher.SubscribeLive(sessionId);
        return pump.BeginLive(sessionId, subscription);
    }

    /// <summary>取消会话。默认 interrupt；<paramref name="mode"/> 为 stop 时停止消费循环。</summary>
    public Task<bool> CancelAsync(string sessionId, CancellationToken cancellationToken = default) =>
        CancelAsync(sessionId, "interrupt", cancellationToken);

    /// <summary>按 mode 分流：interrupt → <see cref="ISessionRuntime.InterruptAsync"/>，stop → <see cref="ISessionRuntime.StopAsync"/>。</summary>
    public async Task<bool> CancelAsync(
        string sessionId,
        string mode,
        CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ISessionRuntime>();
        bool stopped;
        if (string.Equals(mode, "stop", StringComparison.OrdinalIgnoreCase))
        {
            var count = await runtime.StopAsync(sessionId, cancellationToken).ConfigureAwait(false);
            stopped = count > 0;
        }
        else
        {
            stopped = await runtime.InterruptAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        _runTracker.CancelRun(sessionId);
        return stopped;
    }

    private static ChatInput BuildChatInput(GatewayRequest request)
    {
        if (request.Quote != null)
            return BuildChatInputWithQuote(request);

        string? text = null;
        List<ChatAttachment>? attachments = null;

        if (request.Input != null && request.Input.Count > 0)
        {
            var textParts = request.Input
                .OfType<GatewayTextContentPart>()
                .Select(p => p.Text)
                .ToList();

            if (textParts.Count > 0)
                text = string.Join("\n", textParts);

            attachments = new List<ChatAttachment>();
            foreach (var part in request.Input)
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

            if (attachments.Count == 0)
                attachments = null;
        }

        return new ChatInput
        {
            Text = text,
            Attachments = attachments
        };
    }

    /// <summary>
    /// 叠加引用上下文：经 <see cref="GatewayUserMessageComposer"/> 将 <see cref="GatewayRequest.Quote"/>
    /// 与 <see cref="GatewayRequest.Input"/> 合成为带 <c>&lt;quoted_message&gt;</c> 信封的 user turn，
    /// 再映射回 <see cref="ChatInput"/>（文本 + 多模态附件），供 <c>InboxMessage.Body</c>/<c>Attachments</c> 投递。
    /// </summary>
    private static ChatInput BuildChatInputWithQuote(GatewayRequest request)
    {
        var message = GatewayUserMessageComposer.Compose(request.Input, request.Quote);
        if (message is null)
            return new ChatInput();

        var text = message.Content;
        List<ChatAttachment>? attachments = null;

        if (message.Parts is { Count: > 0 })
        {
            var textParts = message.Parts
                .Where(p => p.Type == ContentPartType.Text && !string.IsNullOrEmpty(p.Text))
                .Select(p => p.Text!);
            text = string.Join("\n", textParts);

            attachments = new List<ChatAttachment>();
            foreach (var part in message.Parts)
            {
                if (part.Type == ContentPartType.Image)
                {
                    attachments.Add(new ChatAttachment
                    {
                        Base64Data = part.DataBase64 ?? part.Url ?? "",
                        MimeType = part.MimeType ?? "image/png",
                        FileName = part.FileName ?? ""
                    });
                }
                else if (part.Type == ContentPartType.File)
                {
                    attachments.Add(new ChatAttachment
                    {
                        Base64Data = part.DataBase64 ?? part.Url ?? "",
                        MimeType = part.MimeType ?? "application/octet-stream",
                        FileName = part.FileName ?? ""
                    });
                }
            }

            if (attachments.Count == 0)
                attachments = null;
        }

        return new ChatInput
        {
            Text = text,
            Attachments = attachments
        };
    }
}
