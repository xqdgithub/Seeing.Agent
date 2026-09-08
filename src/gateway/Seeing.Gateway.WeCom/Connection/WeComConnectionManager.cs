using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Seeing.Gateway.WeCom.Connection;

/// <summary>
/// 企微长连接状态机：统一重连策略、epoch 管理、事件分发。
/// </summary>
public sealed class WeComConnectionManager : IAsyncDisposable
{
    private readonly WeComAibotSession _session;
    private readonly WeComOutboundChannel _outbound;
    private readonly ILogger<WeComConnectionManager> _logger;

    private WeComWsClientOptions? _options;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private WeComWebSocketTransport? _transport;
    private CancellationTokenSource? _sessionCts;
    private long _epoch;
    private WeComConnectionState _state = WeComConnectionState.Disconnected;
    private int _superseded;
    private DateTimeOffset? _activeSince;

    public WeComConnectionManager(
        WeComAibotSession session,
        WeComOutboundChannel outbound,
        ILogger<WeComConnectionManager> logger)
    {
        _session = session;
        _outbound = outbound;
        _logger = logger;
    }

    public WeComOutboundChannel Outbound => _outbound;

    public long ConnectionEpoch => Volatile.Read(ref _epoch);

    public WeComConnectionState State => _state;

    public bool IsConnected => _state is WeComConnectionState.Subscribed or WeComConnectionState.Active;

    public event Func<WeComIncomingContext, CancellationToken, Task>? OnMessage;

    public event Func<WeComWsFrame, CancellationToken, Task>? OnEvent;

    public event EventHandler<WeComConnectionChangedEventArgs>? ConnectionChanged;

    public Task StartAsync(WeComWsClientOptions options, CancellationToken cancellationToken = default)
    {
        _options = options;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunForeverAsync(_runCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 收到 disconnected_event 或 Bridge 检测到连接被抢占时调用。
    /// </summary>
    public void NotifySuperseded(string reason)
    {
        Interlocked.Exchange(ref _superseded, 1);
        _sessionCts?.Cancel();
        TransitionTo(WeComConnectionState.Superseded, reason);
    }

    public static string GenerateReqId(string prefix = "req") => $"{prefix}_{Guid.NewGuid():N}";

    internal static int NormalizeMaxReconnectAttempts(int configured) =>
        configured switch
        {
            0 => -1,
            < 0 => -1,
            _ => configured
        };

    internal static TimeSpan CalculateBackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, Math.Pow(2, Math.Min(attempt, 5)))));

    internal static int ResolveHeartbeatSeconds(int configured) =>
        configured > 0 ? configured : 30;

    private async Task RunForeverAsync(CancellationToken cancellationToken)
    {
        if (_options == null)
            throw new InvalidOperationException("WeCom client options not set.");

        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            WeComWebSocketTransport? transport = null;
            try
            {
                attempt++;
                Interlocked.Exchange(ref _superseded, 0);
                TransitionTo(WeComConnectionState.Connecting, null);

                transport = new WeComWebSocketTransport();
                var wsUrl = new Uri(_options.WsUrl);

                _logger.LogInformation("WeCom 连接中: {WsUrl}", wsUrl);
                await transport.ConnectAsync(wsUrl, cancellationToken).ConfigureAwait(false);

                var epoch = Interlocked.Increment(ref _epoch);
                _transport = transport;
                _outbound.Bind(transport, epoch);

                await _session.SubscribeAsync(
                    transport,
                    _options.BotId,
                    _options.Secret,
                    cancellationToken).ConfigureAwait(false);

                TransitionTo(WeComConnectionState.Subscribed, null);
                TransitionTo(WeComConnectionState.Active, "subscribed");

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _sessionCts = sessionCts;
                await TrySendHeartbeatAsync(epoch, sessionCts.Token).ConfigureAwait(false);
                var heartbeatTask = HeartbeatLoopAsync(
                    _options.HeartbeatIntervalSeconds,
                    epoch,
                    sessionCts.Token);

                try
                {
                    await ReceiveLoopAsync(transport, epoch, sessionCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    sessionCts.Cancel();
                    try
                    {
                        await heartbeatTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected
                    }

                    _sessionCts = null;
                }

                attempt = 0;

                if (cancellationToken.IsCancellationRequested)
                    break;

                var reason = Interlocked.CompareExchange(ref _superseded, 0, 0) == 1
                    ? "superseded"
                    : "session_ended";
                await TeardownSessionAsync(transport, reason, TakeConnectedDuration()).ConfigureAwait(false);
                transport = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (transport != null)
                    await TeardownSessionAsync(transport, "cancelled", TakeConnectedDuration()).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                var connectedDuration = TakeConnectedDuration();
                _logger.LogWarning(
                    ex,
                    "WeCom WebSocket 连接失败，尝试重连 #{Attempt}，连接存活 {ConnectedSeconds:F1}s",
                    attempt,
                    connectedDuration?.TotalSeconds);

                if (transport != null)
                    await TeardownSessionAsync(transport, ex.Message, connectedDuration).ConfigureAwait(false);

                var maxAttempts = NormalizeMaxReconnectAttempts(_options.MaxReconnectAttempts);
                if (maxAttempts > 0 && attempt >= maxAttempts)
                {
                    TransitionTo(WeComConnectionState.Failed, $"max reconnect attempts ({maxAttempts})");
                    throw new WeComConnectionFatalException(
                        $"WeCom WebSocket 达到最大重连次数 ({maxAttempts})，停止重连");
                }
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            TransitionTo(WeComConnectionState.Backoff, null);
            var delay = CalculateBackoffDelay(attempt);
            _logger.LogDebug("WeCom 重连退避 {DelaySeconds}s (attempt #{Attempt})", delay.TotalSeconds, attempt);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        TransitionTo(WeComConnectionState.Disconnected, "stopped");
    }

    private async Task TeardownSessionAsync(
        WeComWebSocketTransport transport,
        string reason,
        TimeSpan? connectedDuration = null)
    {
        TransitionTo(WeComConnectionState.Stopping, reason, connectedDuration);
        _outbound.Unbind();
        _transport = null;
        await transport.DisposeAsync().ConfigureAwait(false);
        TransitionTo(WeComConnectionState.Disconnected, reason, connectedDuration);
    }

    private async Task ReceiveLoopAsync(
        WeComWebSocketTransport transport,
        long epoch,
        CancellationToken cancellationToken)
    {
        while (transport.IsOpen && !cancellationToken.IsCancellationRequested)
        {
            var frame = await transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
                break;

            if (string.Equals(frame.Cmd, WeComWsCommands.Pong, StringComparison.OrdinalIgnoreCase)
                || string.Equals(frame.Cmd, WeComWsCommands.Ping, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(frame.Cmd, WeComWsCommands.EventCallback, StringComparison.OrdinalIgnoreCase))
            {
                if (WeComEventParser.TryParseDisconnectedEvent(frame, out _))
                {
                    _logger.LogInformation(
                        "WeCom 收到 disconnected_event（epoch={Epoch}），连接已被新会话取代",
                        epoch);
                    NotifySuperseded("disconnected_event");
                    if (OnEvent != null)
                        await OnEvent.Invoke(frame, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (OnEvent != null)
                    await OnEvent.Invoke(frame, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(frame.Cmd, WeComWsCommands.MsgCallback, StringComparison.OrdinalIgnoreCase))
            {
                _ = DispatchMessageAsync(frame, cancellationToken);
            }
        }
    }

    private Task DispatchMessageAsync(WeComWsFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Body == null || OnMessage == null)
            return Task.CompletedTask;

        var message = frame.Body.Value.Deserialize<WeComIncomingMessage>(WeComWsJson.Options);
        if (message == null)
            return Task.CompletedTask;

        var context = new WeComIncomingContext
        {
            Frame = frame,
            Message = message
        };

        return OnMessage.Invoke(context, cancellationToken);
    }

    private async Task HeartbeatLoopAsync(int intervalSeconds, long epoch, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Clamp(ResolveHeartbeatSeconds(intervalSeconds), 5, 120));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await TrySendHeartbeatAsync(epoch, cancellationToken).ConfigureAwait(false);
    }

    private async Task TrySendHeartbeatAsync(long epoch, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _epoch) != epoch || _transport?.IsOpen != true)
            return;

        try
        {
            await _outbound.SendCommandAsync(
                WeComWsCommands.Ping,
                GenerateReqId("ping"),
                new { },
                epoch,
                cancellationToken,
                WeComOutboundPriority.Ping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WeCom 心跳发送失败，等待接收循环触发重连");
        }
    }

    private TimeSpan? TakeConnectedDuration()
    {
        if (_activeSince == null)
            return null;

        var duration = DateTimeOffset.Now - _activeSince.Value;
        _activeSince = null;
        return duration;
    }

    private void TransitionTo(
        WeComConnectionState newState,
        string? reason,
        TimeSpan? connectedDuration = null)
    {
        var previous = _state;
        if (previous == newState)
            return;

        if (newState == WeComConnectionState.Active)
            _activeSince = DateTimeOffset.Now;

        var duration = connectedDuration;
        if (duration == null
            && newState is WeComConnectionState.Stopping
                or WeComConnectionState.Superseded
                or WeComConnectionState.Failed
            && _activeSince != null)
        {
            duration = DateTimeOffset.Now - _activeSince.Value;
            _activeSince = null;
        }

        _state = newState;
        ConnectionChanged?.Invoke(this, new WeComConnectionChangedEventArgs
        {
            PreviousState = previous,
            CurrentState = newState,
            Epoch = Volatile.Read(ref _epoch),
            Reason = reason,
            ConnectedDuration = duration
        });
    }

    public async ValueTask DisposeAsync()
    {
        _runCts?.Cancel();
        if (_runTask != null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (WeComConnectionFatalException)
            {
                // expected on fatal exit
            }
        }

        if (_transport != null)
            await _transport.DisposeAsync().ConfigureAwait(false);

        _outbound.Unbind();
        _runCts?.Dispose();
        _runCts = null;
    }
}
