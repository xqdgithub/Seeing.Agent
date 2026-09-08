using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Gateway.QQ.Connection;

/// <summary>
/// QQ Bot WebSocket 客户端（收事件；intent 降级 / 快速断线 / can_resume）。
/// </summary>
public sealed class QQWebSocketClient : IAsyncDisposable
{
    private static readonly int[] s_reconnectDelays = [1, 2, 5, 10, 30, 60];
    private const int RateLimitDelaySeconds = 60;
    private const double QuickDisconnectThresholdSeconds = 5;
    private const int MaxQuickDisconnectCount = 3;

    private readonly QQOptions _options;
    private readonly QQAccessTokenProvider _tokenProvider;
    private readonly QQHttpApiClient _api;
    private readonly ILogger<QQWebSocketClient> _logger;
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private Task? _runTask;
    private string? _sessionId;
    private int? _lastSeq;
    private int _heartbeatMs = 40000;
    private int _identifyFailCount;
    private int _quickDisconnectCount;
    private int _reconnectAttempts;
    private DateTimeOffset? _lastConnectTime;
    private DateTimeOffset? _lastReadyAt;
    private volatile bool _connected;

    public event Func<string, JsonElement, CancellationToken, Task>? OnDispatch;

    /// <summary>当前是否已建立 WS 连接（供健康检查）。</summary>
    public bool IsConnected => _connected && _ws?.State == WebSocketState.Open;

    public DateTimeOffset? LastReadyAt => _lastReadyAt;

    public int IdentifyFailCount => _identifyFailCount;

    public QQWebSocketClient(
        IOptions<QQOptions> options,
        QQAccessTokenProvider tokenProvider,
        QQHttpApiClient api,
        ILogger<QQWebSocketClient> logger)
    {
        _options = options.Value;
        _tokenProvider = tokenProvider;
        _api = api;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunForeverAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runTask != null)
        {
            try { await _runTask.ConfigureAwait(false); } catch { /* ignore */ }
        }

        _connected = false;
        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None);
            }
            catch { /* ignore */ }
            _ws.Dispose();
            _ws = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunForeverAsync(CancellationToken cancellationToken)
    {
        var max = _options.MaxReconnectAttempts;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                _reconnectAttempts = 0;
                _quickDisconnectCount = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _connected = false;
                _logger.LogWarning(ex, "QQ WebSocket disconnected (attempt {Attempt})", _reconnectAttempts + 1);

                var delay = ComputeReconnectDelay();
                _reconnectAttempts++;
                if (max >= 0 && _reconnectAttempts > max)
                    break;

                _logger.LogInformation("QQ reconnecting in {Delay}s (attempt {Attempt})", delay.TotalSeconds, _reconnectAttempts);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeReconnectDelay()
    {
        var elapsed = _lastConnectTime.HasValue
            ? (DateTimeOffset.Now - _lastConnectTime.Value).TotalSeconds
            : (double?)null;

        if (elapsed is < QuickDisconnectThresholdSeconds)
        {
            _quickDisconnectCount++;
            if (_quickDisconnectCount >= MaxQuickDisconnectCount)
            {
                _sessionId = null;
                _lastSeq = null;
                _tokenProvider.Invalidate();
                _quickDisconnectCount = 0;
                _reconnectAttempts = Math.Min(_reconnectAttempts, s_reconnectDelays.Length - 1);
                _logger.LogWarning("QQ rapid disconnect threshold reached; clearing session and backing off");
                return TimeSpan.FromSeconds(RateLimitDelaySeconds);
            }
        }
        else
        {
            _quickDisconnectCount = 0;
        }

        var idx = Math.Min(_reconnectAttempts, s_reconnectDelays.Length - 1);
        return TimeSpan.FromSeconds(s_reconnectDelays[idx]);
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        var url = await _api.GetGatewayUrlAsync(cancellationToken).ConfigureAwait(false);
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        _lastConnectTime = DateTimeOffset.Now;
        _connected = true;
        _logger.LogInformation("QQ WebSocket connected: {Url}", url);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatLoopAsync(heartbeatCts.Token);

        try
        {
            await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connected = false;
            heartbeatCts.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); } catch { /* ignore */ }
            _ws?.Dispose();
            _ws = null;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (_ws is { State: WebSocketState.Open } && !cancellationToken.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using var doc = JsonDocument.Parse(ms.ToArray());
            await HandleFrameAsync(doc.RootElement, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFrameAsync(JsonElement frame, CancellationToken cancellationToken)
    {
        var op = frame.GetProperty("op").GetInt32();
        if (frame.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number)
            _lastSeq = s.GetInt32();

        switch (op)
        {
            case QQOpcodes.Hello:
                _heartbeatMs = frame.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                await IdentifyOrResumeAsync(cancellationToken).ConfigureAwait(false);
                break;

            case QQOpcodes.Dispatch:
                {
                    var t = frame.GetProperty("t").GetString() ?? "";
                    var d = frame.GetProperty("d").Clone(); // 避免后续帧覆盖 / 文档释放后悬空
                    if (string.Equals(t, "READY", StringComparison.OrdinalIgnoreCase))
                    {
                        _sessionId = d.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                        _identifyFailCount = 0;
                        _reconnectAttempts = 0;
                        _lastReadyAt = DateTimeOffset.Now;
                        _logger.LogInformation(
                            "QQ READY session_id={SessionId} identifyFailCount={Fail} intents={Intents}",
                            _sessionId,
                            _identifyFailCount,
                            ComputeIntents());
                    }
                    else if (string.Equals(t, "RESUMED", StringComparison.OrdinalIgnoreCase))
                    {
                        _identifyFailCount = 0;
                        _reconnectAttempts = 0;
                        _logger.LogInformation("QQ RESUMED");
                    }
                    else
                    {
                        // 所有业务事件都打点，便于确认群聊事件是否到达
                        _logger.LogInformation("QQ dispatch t={EventType}", t);
                        if (OnDispatch != null)
                            await OnDispatch.Invoke(t, d, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                }

            case QQOpcodes.HeartbeatAck:
                break;

            case QQOpcodes.Reconnect:
                throw new InvalidOperationException("QQ server requested reconnect");

            case QQOpcodes.InvalidSession:
                {
                    var canResume = frame.TryGetProperty("d", out var dEl)
                                    && dEl.ValueKind == JsonValueKind.True;
                    _logger.LogError("QQ invalid session can_resume={CanResume}", canResume);
                    if (!canResume)
                    {
                        _sessionId = null;
                        _lastSeq = null;
                        _identifyFailCount++;
                        _tokenProvider.Invalidate();
                    }
                    throw new InvalidOperationException($"QQ invalid session can_resume={canResume}");
                }
        }
    }

    private async Task IdentifyOrResumeAsync(CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        object payload;
        int op;
        if (!string.IsNullOrEmpty(_sessionId) && _lastSeq != null)
        {
            op = QQOpcodes.Resume;
            payload = new { token = $"QQBot {token}", session_id = _sessionId, seq = _lastSeq };
        }
        else
        {
            op = QQOpcodes.Identify;
            payload = new
            {
                token = $"QQBot {token}",
                intents = ComputeIntents(),
                shard = new[] { 0, 1 }
            };
        }

        await SendAsync(new { op, d = payload }, cancellationToken).ConfigureAwait(false);
    }

    private int ComputeIntents()
    {
        // 注意：GUILD_MEMBERS 为特权 Intent，未开通时会导致 Identify/InvalidSession，
        // 进而触发降级并丢掉 GroupAndC2C —— 群聊与 C2C 都会受影响。默认不订阅。
        var intents = QQIntents.PublicGuildMessages | QQIntents.Interaction;
        if (_identifyFailCount < 3)
            intents |= QQIntents.DirectMessage | QQIntents.GroupAndC2C;
        else
            _logger.LogWarning(
                "QQ identify fail count={Count}; degrading intents (guild public + interaction only)",
                _identifyFailCount);
        return intents;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatMs, cancellationToken).ConfigureAwait(false);
            await SendAsync(new { op = QQOpcodes.Heartbeat, d = _lastSeq }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        if (_ws is not { State: WebSocketState.Open })
            return;
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
}
