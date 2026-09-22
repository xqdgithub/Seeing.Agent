using System.Threading.Channels;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Tui.Services;

public interface ITuiEventPump : IAsyncDisposable
{
    string SessionId { get; }
    ChannelReader<IMessageEvent> Reader { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

/// <summary>
/// 单会话事件泵：订阅 <see cref="IChatOrchestrator.SubscribeEvents"/>（订阅时自动回放环形缓冲），泵入通道。
/// <para>
/// 通道刻意<b>无界</b>：事件是执行正确性的载体，丢弃会破坏账本/视图一致性；
/// 故内存有界性由消费端持续排空保证（TUI 主编循环与 task 追踪消费循环均持续读取）。
/// </para>
/// </summary>
public sealed class TuiEventPump : ITuiEventPump
{
    private readonly IChatOrchestrator _orchestrator;
    private readonly Channel<IMessageEvent> _channel =
        Channel.CreateUnbounded<IMessageEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TuiEventPump(IChatOrchestrator orchestrator, string sessionId)
    {
        _orchestrator = orchestrator;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public ChannelReader<IMessageEvent> Reader => _channel.Reader;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _orchestrator.SubscribeEvents(SessionId, _cts.Token))
                    await _channel.Writer.WriteAsync(evt, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                _channel.Writer.TryComplete(ex);
                return;
            }

            _channel.Writer.TryComplete();
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        _cts = null;
    }
}
