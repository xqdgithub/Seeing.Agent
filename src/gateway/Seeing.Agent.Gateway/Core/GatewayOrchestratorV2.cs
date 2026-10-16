using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Permission;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// Gateway 会话编排器 — Deliver / Subscribe / Cancel（会话级，对齐 ISessionRuntime）。
/// </summary>
public sealed class GatewayOrchestratorV2
{
    /// <summary>空闲后订阅收口前的尾部排空窗口（尽力转发环形缓冲尾部事件）。</summary>
    private static readonly TimeSpan IdleDrainGrace = TimeSpan.FromMilliseconds(50);

    private readonly IServiceProvider _services;
    private readonly GatewayOptions _options;
    private readonly GatewayRunTracker _runTracker;
    private readonly SessionExecutionQueue _executionQueue;
    private readonly ILogger<GatewayOrchestratorV2> _logger;

    public GatewayOrchestratorV2(
        IServiceProvider services,
        GatewayOptions options,
        GatewayRunTracker runTracker,
        SessionExecutionQueue executionQueue,
        ILogger<GatewayOrchestratorV2> logger)
    {
        _services = services;
        _options = options;
        _runTracker = runTracker;
        _executionQueue = executionQueue;
        _logger = logger;
    }

    /// <summary>提交执行（非阻塞）：以 Human 用户消息投递并唤醒会话消费循环。</summary>
    public async Task<GatewaySubmitResult> SubmitAsync(
        GatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var sessionId = request.SessionId;
        var channelId = request.ChannelId ?? "default";

        await _executionQueue.WaitAsync(channelId, sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
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
        finally
        {
            _executionQueue.Release(channelId, sessionId);
        }
    }

    /// <summary>
    /// 会话级订阅：流式转发会话事件，直到消费循环空闲（<see cref="ISessionRuntime.WaitForIdleAsync"/> 语义）
    /// 或取消/超时。不再按 executionId 过滤。
    /// </summary>
    public async IAsyncEnumerable<GatewayEvent> SubscribeSessionEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 每订阅一个独立呈现端实例：注册/注销与订阅同生命周期，等价于原引用计数语义；
        // 同 session 多订阅各持一份，任一订阅结束不影响其它（spec §4.6 / N2）。
        var presentation = _services.GetRequiredService<IPermissionSurfaceRegistry>();
        var presenter = new GatewaySubscriptionPresenter(sessionId);
        presentation.Register(presenter);
        try
        {
            var runCts = _runTracker.RegisterRun(sessionId);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCts.Token);

            var runState = new ChatRunState();
            Exception? fault = null;
            var cancelled = false;

            await using var enumerator = StreamSessionEventsAsync(sessionId, linkedCts.Token, runState)
                .GetAsyncEnumerator(linkedCts.Token);

            while (true)
            {
                var hasNext = false;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    fault = ex;
                    break;
                }

                if (!hasNext)
                    break;

                yield return enumerator.Current;
            }

            _runTracker.UnregisterRun(sessionId);

            if (cancelled)
                yield return await BuildCancelledEventAsync(sessionId);
            else if (fault != null)
            {
                _logger.LogError(fault, "Gateway 会话订阅失败: SessionId={SessionId}", sessionId);
                yield return await BuildErrorEventAsync(sessionId, fault);
            }
        }
        finally
        {
            presentation.Unregister(presenter);
        }
    }

    /// <summary>取消会话（异步）：停止会话消费循环并取消在途执行。</summary>
    public async Task<bool> CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ISessionRuntime>();
        var stopped = await runtime.StopAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _runTracker.CancelRun(sessionId);
        return stopped > 0;
    }

    private async IAsyncEnumerable<GatewayEvent> StreamSessionEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ChatRunState runState)
    {
        using var scope = _services.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ISessionRuntime>();

        var outputChannel = Channel.CreateUnbounded<GatewayEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pumpTask = Task.Run(
            () => PumpSessionEventsAsync(runtime, sessionId, runState, outputChannel.Writer, pumpCts.Token),
            CancellationToken.None);

        // 会话空闲即收口：空闲后留短暂窗口排空环形缓冲尾部，再完成通道并停止订阅泵。
        var idleTask = Task.Run(async () =>
        {
            try
            {
                await runtime.WaitForIdleAsync(sessionId, cancellationToken).ConfigureAwait(false);
                await Task.Delay(IdleDrainGrace, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 订阅被取消（用户取消 / 连接断开）
            }
            finally
            {
                outputChannel.Writer.TryComplete();
                TryCancel(pumpCts);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var gatewayEvent in outputChannel.Reader.ReadAllAsync(cancellationToken))
                yield return gatewayEvent;
        }
        finally
        {
            outputChannel.Writer.TryComplete();
            TryCancel(pumpCts);
            await AwaitQuietlyAsync(pumpTask).ConfigureAwait(false);
            await AwaitQuietlyAsync(idleTask).ConfigureAwait(false);
        }
    }

    private async Task PumpSessionEventsAsync(
        ISessionRuntime runtime,
        string sessionId,
        ChatRunState runState,
        ChannelWriter<GatewayEvent> outputWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chatEvent in runtime.SubscribeEvents(sessionId, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (chatEvent.LoopId != null)
                    runState.CurrentLoopId = chatEvent.LoopId;

                if (chatEvent is SessionUpdatedEvent sessionEvt)
                    runState.Session = sessionEvt.Session;

                if (GatewayEventMapper.TryMap(chatEvent, out var gatewayEvent) && gatewayEvent != null)
                {
                    await outputWriter.WriteAsync(gatewayEvent, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 订阅收口 / 取消：正常结束
        }
        finally
        {
            outputWriter.TryComplete();
            await PersistSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    /// <summary>订阅结束前落盘：确保本轮会话状态已持久化；写回失败不得中断事件流收尾。</summary>
    private async Task PersistSessionAsync(string sessionId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
            await sessionManager.SaveAsync(sessionId).ConfigureAwait(false);
            await sessionManager.FlushAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "订阅结束落盘失败，已跳过: SessionId={SessionId}", sessionId);
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // 收尾任务异常隔离：订阅主路径不得因此中断
        }
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

    private async Task<GatewayEvent> BuildCancelledEventAsync(string sessionId)
    {
        // spec §9：取消横幅保留持久化、不唤醒（Sender=System, Presentation=None, Delivery=Inject）
        await DeliverSystemBannerAsync(sessionId, "⚠️ 执行已取消").ConfigureAwait(false);

        return new GatewayEvent
        {
            Object = GatewayEventObject.Response,
            Status = GatewayEventStatus.Cancelled,
            SessionId = sessionId,
            Data = new GatewayEventData { CancelReason = "用户取消或连接断开" }
        };
    }

    private async Task<GatewayEvent> BuildErrorEventAsync(string sessionId, Exception ex)
    {
        // spec §9：错误横幅保留持久化、不唤醒（Sender=System, Presentation=None, Delivery=Inject）
        await DeliverSystemBannerAsync(sessionId, $"❌ 执行出错: {ex.Message}").ConfigureAwait(false);

        return new GatewayEvent
        {
            Object = GatewayEventObject.Error,
            Status = GatewayEventStatus.Failed,
            SessionId = sessionId,
            Data = new GatewayEventData
            {
                Error = ex.Message,
                ErrorSource = "gateway.orchestrator"
            }
        };
    }

    private async Task DeliverSystemBannerAsync(string sessionId, string body)
    {
        try
        {
            using var scope = _services.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<ISessionRuntime>();
            await runtime.DeliverAsync(
                new InboxMessage
                {
                    TargetSessionId = sessionId,
                    Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "gateway" },
                    Body = body,
                    Delivery = InboxDelivery.Inject,
                    Presentation = InboxPresentation.None,
                    SourceTag = "gateway"
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway 横幅投递失败，已跳过: SessionId={SessionId}", sessionId);
        }
    }

    private sealed class ChatRunState
    {
        public SessionData? Session { get; set; }
        public string? CurrentLoopId { get; set; }
    }
}
