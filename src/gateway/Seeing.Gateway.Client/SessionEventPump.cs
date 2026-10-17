using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Seeing.Gateway.Client;

/// <summary>
/// 会话事件泵：订阅级呈现端注册、空闲后 50ms 排空、经 <see cref="GatewayEventMapper.TryMap"/> 映射。
/// 取消合成 <see cref="GatewayEventStatus.Cancelled"/>，枚举异常合成 Error。不落盘。
/// <para>
/// 两种订阅模式：
/// <list type="bullet">
/// <item><see cref="StreamAsync"/>（回放）：UI/SSE 重连语义，订阅时回放环形缓冲历史；</item>
/// <item><see cref="BeginLive(string, ILiveEventSubscription)"/>（实时）：请求/应答语义，
/// 不回放；必须在触发执行（投递唤醒）之前调用完成注册，保证整轮事件自 LoopStart 起不漏。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SessionEventPump
{
    /// <summary>空闲后订阅收口前的尾部排空窗口（尽力转发环形缓冲尾部事件）。</summary>
    private static readonly TimeSpan IdleDrainGrace = TimeSpan.FromMilliseconds(50);

    private readonly ISessionRuntime _runtime;
    private readonly GatewayRunTracker _runTracker;
    private readonly IPermissionSurfaceRegistry _presentation;
    private readonly ILogger? _logger;

    public SessionEventPump(
        ISessionRuntime runtime,
        GatewayRunTracker runTracker,
        IPermissionSurfaceRegistry presentation,
        ILogger? logger = null)
    {
        _runtime = runtime;
        _runTracker = runTracker;
        _presentation = presentation;
        _logger = logger;
    }

    /// <summary>回放订阅流（UI/SSE 重连语义）：订阅时回放缓冲历史，空闲后 50ms 排空收口。</summary>
    public async IAsyncEnumerable<GatewayEvent> StreamAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var presenter = new GatewaySubscriptionPresenter(sessionId);
        _presentation.Register(presenter);
        var (runId, runToken) = _runTracker.RegisterRun(sessionId);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);

            var source = _runtime.SubscribeEvents(sessionId, linkedCts.Token);
            await foreach (var gatewayEvent in StreamUntilIdleAsync(sessionId, source, linkedCts.Token)
                               .ConfigureAwait(false))
            {
                yield return gatewayEvent;
            }
        }
        finally
        {
            _runTracker.UnregisterRun(sessionId, runId);
            _presentation.Unregister(presenter);
        }
    }

    /// <summary>
    /// 请求/应答实时流：立即同步注册（呈现端 + run 跟踪，实时订阅由调用方先经
    /// <see cref="IExecutionEventPublisher.SubscribeLive"/> 建立），不回放。
    /// 必须在触发执行（<c>SubmitAsync</c> 投递唤醒）之前调用，保证整轮事件自 LoopStart 起不漏；
    /// 配合 <see cref="Seeing.Gateway.Channels.GatewayAssistantReplyCollector"/> 轮次门控忽略上一轮尾流。
    /// </summary>
    public LiveSessionEventStream BeginLive(string sessionId, ILiveEventSubscription subscription)
    {
        var presenter = new GatewaySubscriptionPresenter(sessionId);
        _presentation.Register(presenter);
        var (runId, runToken) = _runTracker.RegisterRun(sessionId);
        return new LiveSessionEventStream(this, sessionId, subscription, presenter, runId, runToken);
    }

    /// <summary>
    /// 核心：消费事件源 → 映射转发 → 消费循环空闲（<see cref="ISessionRuntime.WaitForIdleAsync"/> 语义）
    /// + 50ms 排空收口；取消合成 <see cref="GatewayEventStatus.Cancelled"/>，枚举异常合成 Error。
    /// </summary>
    internal async IAsyncEnumerable<GatewayEvent> StreamUntilIdleAsync(
        string sessionId,
        IAsyncEnumerable<IMessageEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var outputChannel = Channel.CreateUnbounded<GatewayEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pumpTask = Task.Run(
            () => PumpSessionEventsAsync(source, outputChannel.Writer, pumpCts.Token),
            CancellationToken.None);

        var idleTask = Task.Run(async () =>
        {
            try
            {
                await _runtime.WaitForIdleAsync(sessionId, cancellationToken).ConfigureAwait(false);
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

        Exception? fault = null;
        var cancelled = false;

        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await outputChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
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

                while (outputChannel.Reader.TryRead(out var gatewayEvent))
                    yield return gatewayEvent;
            }
        }
        finally
        {
            outputChannel.Writer.TryComplete();
            TryCancel(pumpCts);
            await AwaitQuietlyAsync(pumpTask).ConfigureAwait(false);
            await AwaitQuietlyAsync(idleTask).ConfigureAwait(false);
        }

        if (cancelled)
            yield return await BuildCancelledEventAsync(sessionId).ConfigureAwait(false);
        else if (fault != null)
        {
            _logger?.LogError(fault, "Gateway 会话订阅失败: SessionId={SessionId}", sessionId);
            yield return await BuildErrorEventAsync(sessionId, fault).ConfigureAwait(false);
        }
    }

    private async Task PumpSessionEventsAsync(
        IAsyncEnumerable<IMessageEvent> source,
        ChannelWriter<GatewayEvent> outputWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chatEvent in source)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (GatewayEventMapper.TryMap(chatEvent, out var gatewayEvent) && gatewayEvent != null)
                    await outputWriter.WriteAsync(gatewayEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 订阅收口 / 取消：正常结束
        }
        finally
        {
            outputWriter.TryComplete();
        }
    }

    private async Task<GatewayEvent> BuildCancelledEventAsync(string sessionId)
    {
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
            await _runtime.DeliverAsync(
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
            _logger?.LogWarning(ex, "Gateway 横幅投递失败，已跳过: SessionId={SessionId}", sessionId);
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

    internal void FinishLive(
        string sessionId,
        GatewaySubscriptionPresenter presenter,
        string runId,
        ILiveEventSubscription subscription)
    {
        _runTracker.UnregisterRun(sessionId, runId);
        _presentation.Unregister(presenter);
        subscription.Dispose();
    }
}

/// <summary>
/// 请求/应答实时流句柄：<see cref="SessionEventPump.BeginLive(string, ILiveEventSubscription)"/> 的返回值。
/// 枚举 <see cref="StreamAsync"/>（投递触发之后）开始消费；结束或 <see cref="Dispose"/> 时自动注销全部注册。
/// </summary>
public sealed class LiveSessionEventStream : IDisposable
{
    private readonly SessionEventPump _pump;
    private readonly string _sessionId;
    private readonly ILiveEventSubscription _subscription;
    private readonly GatewaySubscriptionPresenter _presenter;
    private readonly string _runId;
    private readonly CancellationToken _runToken;
    private int _disposed;

    internal LiveSessionEventStream(
        SessionEventPump pump,
        string sessionId,
        ILiveEventSubscription subscription,
        GatewaySubscriptionPresenter presenter,
        string runId,
        CancellationToken runToken)
    {
        _pump = pump;
        _sessionId = sessionId;
        _subscription = subscription;
        _presenter = presenter;
        _runId = runId;
        _runToken = runToken;
    }

    /// <summary>流式转发实时订阅事件，直到消费循环空闲（含 50ms 排空）或取消/异常。</summary>
    public async IAsyncEnumerable<GatewayEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runToken);
            await foreach (var gatewayEvent in _pump.StreamUntilIdleAsync(_sessionId, _subscription, linkedCts.Token)
                               .ConfigureAwait(false))
            {
                yield return gatewayEvent;
            }
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _pump.FinishLive(_sessionId, _presenter, _runId, _subscription);
    }
}
