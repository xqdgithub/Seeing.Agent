using Microsoft.Extensions.Logging;
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

            Exception? fault = null;
            var cancelled = false;

            await using var enumerator = StreamSessionEventsAsync(sessionId, linkedCts.Token)
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

            if (cancelled)
                yield return await BuildCancelledEventAsync(sessionId).ConfigureAwait(false);
            else if (fault != null)
            {
                _logger?.LogError(fault, "Gateway 会话订阅失败: SessionId={SessionId}", sessionId);
                yield return await BuildErrorEventAsync(sessionId, fault).ConfigureAwait(false);
            }
        }
        finally
        {
            _runTracker.UnregisterRun(sessionId, runId);
            _presentation.Unregister(presenter);
        }
    }

    private async IAsyncEnumerable<GatewayEvent> StreamSessionEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var outputChannel = Channel.CreateUnbounded<GatewayEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pumpTask = Task.Run(
            () => PumpSessionEventsAsync(sessionId, outputChannel.Writer, pumpCts.Token),
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
        string sessionId,
        ChannelWriter<GatewayEvent> outputWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chatEvent in _runtime.SubscribeEvents(sessionId, cancellationToken))
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
}
