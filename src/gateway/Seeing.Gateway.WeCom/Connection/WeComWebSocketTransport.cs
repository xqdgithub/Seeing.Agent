using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Seeing.Gateway.WeCom.Connection;

/// <summary>
/// 纯 WebSocket 传输层：建连、关闭、收发帧。
/// </summary>
public sealed class WeComWebSocketTransport : IAsyncDisposable
{
    private ClientWebSocket? _webSocket;

    public bool IsOpen => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri wsUrl, CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);

        // 企微长连接心跳使用应用层 JSON ping；禁用 .NET WebSocket 协议级 keepalive，避免与企微协议冲突。
        _webSocket = new ClientWebSocket
        {
            Options = { KeepAliveInterval = Timeout.InfiniteTimeSpan }
        };

        await _webSocket.ConnectAsync(wsUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WeCom WebSocket 未连接");

        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WeComWsFrame?> ReceiveFrameAsync(CancellationToken cancellationToken)
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

    public async Task CloseAsync()
    {
        if (_webSocket == null)
            return;

        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "close", closeCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                _webSocket.Abort();
            }
            catch
            {
                // ignore
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _webSocket?.Dispose();
        _webSocket = null;
    }
}
