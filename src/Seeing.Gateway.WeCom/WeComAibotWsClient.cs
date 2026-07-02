using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微 AI Bot WebSocket 客户端：订阅、心跳、重连、流式回复
/// </summary>
public sealed class WeComAibotWsClient : IAsyncDisposable
{
    private const string ProcessingText = "🤔 Thinking...";

    private readonly ILogger<WeComAibotWsClient> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private WeComWsClientOptions? _options;

    public WeComAibotWsClient(ILogger<WeComAibotWsClient> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public event Func<WeComIncomingContext, CancellationToken, Task>? OnMessage;

    public event Func<WeComWsFrame, CancellationToken, Task>? OnEvent;

    /// <summary>建立连接并启动接收/心跳循环</summary>
    public Task ConnectAsync(WeComWsClientOptions options, CancellationToken cancellationToken = default)
    {
        _options = options;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunForeverAsync(_runCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>发送流式文本回复</summary>
    public Task ReplyStreamAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        bool finish,
        CancellationToken cancellationToken = default)
    {
        var body = new WeComRespondStreamBody
        {
            Stream = new WeComStreamPayload
            {
                Id = streamId,
                Content = content,
                Finish = finish
            }
        };

        return SendCommandAsync(
            WeComWsCommands.RespondMsg,
            requestFrame.Headers?.ReqId,
            body,
            cancellationToken);
    }

    /// <summary>回复 enter_chat 欢迎语（须在 5 秒内调用）</summary>
    public Task ReplyWelcomeAsync(
        WeComWsFrame eventFrame,
        string text,
        CancellationToken cancellationToken = default)
    {
        var body = new WeComWelcomeBody
        {
            Text = new WeComTextPayload { Content = text }
        };

        return SendCommandAsync(
            WeComWsCommands.RespondWelcome,
            eventFrame.Headers?.ReqId,
            body,
            cancellationToken);
    }

    /// <summary>发送模板卡片（权限确认等）</summary>
    public Task ReplyTemplateCardAsync(
        WeComWsFrame requestFrame,
        WeComTemplateCardRespondBody body,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            WeComWsCommands.RespondMsg,
            requestFrame.Headers?.ReqId,
            body,
            cancellationToken);

    /// <summary>更新模板卡片状态</summary>
    public Task ReplyUpdateTemplateCardAsync(
        WeComWsFrame eventFrame,
        WeComTemplateCardUpdateBody body,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            WeComWsCommands.RespondUpdate,
            eventFrame.Headers?.ReqId,
            body,
            cancellationToken);

    /// <summary>发送 Thinking 占位流</summary>
    public Task SendProcessingIndicatorAsync(
        WeComWsFrame requestFrame,
        string streamId,
        CancellationToken cancellationToken = default) =>
        ReplyStreamAsync(requestFrame, streamId, ProcessingText, finish: false, cancellationToken);

    public static string GenerateStreamId() => $"stream_{Guid.NewGuid():N}";

    public static string GenerateReqId(string prefix = "req") => $"{prefix}_{Guid.NewGuid():N}";

    private async Task RunForeverAsync(CancellationToken cancellationToken)
    {
        if (_options == null)
            throw new InvalidOperationException("WeCom client options not set.");

        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(_options, cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogWarning(ex, "WeCom WebSocket 连接失败，尝试重连 #{Attempt}", attempt);

                if (_options.MaxReconnectAttempts >= 0 && attempt >= _options.MaxReconnectAttempts)
                {
                    _logger.LogError("WeCom WebSocket 达到最大重连次数，停止重连");
                    break;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConnectOnceAsync(WeComWsClientOptions options, CancellationToken cancellationToken)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        _logger.LogInformation("WeCom 连接中: {WsUrl}", options.WsUrl);
        await _webSocket.ConnectAsync(new Uri(options.WsUrl), cancellationToken).ConfigureAwait(false);

        await SubscribeAsync(options, cancellationToken).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatLoopAsync(options.HeartbeatIntervalSeconds, linkedCts.Token);
        try
        {
            await ReceiveLoopAsync(linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    private async Task SubscribeAsync(WeComWsClientOptions options, CancellationToken cancellationToken)
    {
        var reqId = GenerateReqId("sub");
        await SendCommandAsync(
            WeComWsCommands.Subscribe,
            reqId,
            new WeComSubscribeBody { BotId = options.BotId, Secret = options.Secret },
            cancellationToken).ConfigureAwait(false);

        var response = await ReceiveSingleFrameAsync(cancellationToken).ConfigureAwait(false);
        if (response == null)
            throw new InvalidOperationException("WeCom 订阅无响应");

        if (response.ErrCode != 0)
            throw new InvalidOperationException($"WeCom 订阅失败: errcode={response.ErrCode}, errmsg={response.ErrMsg}");

        _logger.LogInformation("WeCom 订阅成功");
    }

    private async Task HeartbeatLoopAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, intervalSeconds));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_webSocket?.State != WebSocketState.Open)
                continue;

            await SendCommandAsync(WeComWsCommands.Ping, GenerateReqId("ping"), new { }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var frame = await ReceiveSingleFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
                break;

            if (string.Equals(frame.Cmd, WeComWsCommands.Pong, StringComparison.OrdinalIgnoreCase)
                || string.Equals(frame.Cmd, WeComWsCommands.Ping, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(frame.Cmd, WeComWsCommands.MsgCallback, StringComparison.OrdinalIgnoreCase))
            {
                await DispatchMessageAsync(frame, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(frame.Cmd, WeComWsCommands.EventCallback, StringComparison.OrdinalIgnoreCase))
            {
                if (OnEvent != null)
                    await OnEvent.Invoke(frame, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchMessageAsync(WeComWsFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Body == null || OnMessage == null)
            return;

        var message = frame.Body.Value.Deserialize<WeComIncomingMessage>(WeComWsJson.Options);
        if (message == null)
            return;

        var context = new WeComIncomingContext
        {
            Frame = frame,
            Message = message
        };

        await OnMessage.Invoke(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCommandAsync(
        string cmd,
        string? reqId,
        object body,
        CancellationToken cancellationToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WeCom WebSocket 未连接");

        var frame = new WeComWsFrame
        {
            Cmd = cmd,
            Headers = new WeComWsHeaders { ReqId = reqId ?? GenerateReqId() },
            Body = JsonSerializer.SerializeToElement(body, WeComWsJson.Options)
        };

        var json = JsonSerializer.Serialize(frame, WeComWsJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<WeComWsFrame?> ReceiveSingleFrameAsync(CancellationToken cancellationToken)
    {
        if (_webSocket == null)
            return null;

        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();

        while (_webSocket.State == WebSocketState.Open)
        {
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            var json = Encoding.UTF8.GetString(message.ToArray());
            return JsonSerializer.Deserialize<WeComWsFrame>(json, WeComWsJson.Options);
        }

        return null;
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
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            _webSocket.Dispose();
            _webSocket = null;
        }

        _runCts?.Dispose();
        _runCts = null;
    }
}

/// <summary>
/// 入站消息上下文（保留原始 frame 供 reply_stream 回传 req_id）
/// </summary>
public sealed class WeComIncomingContext
{
    public required WeComWsFrame Frame { get; init; }

    public required WeComIncomingMessage Message { get; init; }
}

public sealed class WeComWelcomeBody
{
    [JsonPropertyName("msgtype")]
    public string MsgType { get; init; } = "text";

    [JsonPropertyName("text")]
    public required WeComTextPayload Text { get; init; }
}
