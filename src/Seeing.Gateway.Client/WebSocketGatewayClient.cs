using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Gateway.Client;

/// <summary>
/// 基于 WebSocket 的 <see cref="IGatewayConnection"/> 实现
/// </summary>
public sealed class WebSocketGatewayClient : IGatewayConnection
{
    private readonly GatewayClientOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private readonly ConcurrentQueue<GatewayInbound> _inboundQueue = new();
    private readonly SemaphoreSlim _inboundSignal = new(0);

    public WebSocketGatewayClient(IOptions<GatewayClientOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        var wsUri = BuildWebSocketUri(_options.BaseUrl, _options.WebSocketPath);
        _webSocket = new ClientWebSocket();
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _webSocket.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(false);
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
    }

    public async Task<string> SendChatAsync(GatewayRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        var frame = GatewayWsFrameSerializer.Create(GatewayWsFrameType.Chat, requestId, request);
        await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        return requestId;
    }

    public async IAsyncEnumerable<GatewayInbound> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            GatewayInbound? inbound = null;

            while (_inboundQueue.TryDequeue(out var queued))
            {
                inbound = queued;
                break;
            }

            if (inbound == null)
            {
                try
                {
                    await _inboundSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (!_inboundQueue.TryDequeue(out inbound))
                    continue;
            }

            yield return inbound;
        }
    }

    public async Task StopChatAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.Stop,
            Guid.NewGuid().ToString("N"),
            new GatewayStopPayload { SessionId = sessionId });
        await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        bool allow,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.PermissionRespond,
            requestId,
            new GatewayPermissionRespondPayload
            {
                SessionId = sessionId,
                PermissionId = permissionId,
                Allow = allow,
                Reason = reason
            });

        await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        await foreach (var inbound in ReceiveAsync(timeoutCts.Token).ConfigureAwait(false))
        {
            if (inbound.Type == GatewayWsFrameType.PermissionAck && inbound.Id == requestId)
                return inbound.PermissionAck ?? GatewayPermissionRespondResult.Fail("Empty permission ack");

            if (inbound.Type is GatewayWsFrameType.Error or GatewayWsFrameType.ChatError)
                return GatewayPermissionRespondResult.Fail(inbound.Error?.Message ?? "Permission respond failed");
        }

        return GatewayPermissionRespondResult.Fail("Permission respond timed out");
    }

    public async Task<GatewaySessionResetResult> ResetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Gateway BaseUrl is required.");

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = _options.Timeout
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);

        var encodedSessionId = Uri.EscapeDataString(sessionId);
        using var response = await httpClient.PostAsync(
            $"api/gateway/sessions/{encodedSessionId}/reset",
            content: null,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Session not found: {sessionId}");

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GatewaySessionResetResult>(
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("Empty reset response body");
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts?.Cancel();

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore
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

        _receiveCts?.Dispose();
        _receiveCts = null;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(GatewayWsFrame frame, CancellationToken cancellationToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        var json = GatewayWsFrameSerializer.Serialize(frame);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_webSocket == null)
            return;

        try
        {
            await foreach (var frame in GatewayWsFrameReader.ReadFramesAsync(_webSocket, cancellationToken)
                               .ConfigureAwait(false))
            {
                var inbound = GatewayInboundParser.Parse(frame);
                _inboundQueue.Enqueue(inbound);
                _inboundSignal.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (WebSocketException)
        {
            // connection closed
        }
    }

    private static Uri BuildWebSocketUri(string baseUrl, string webSocketPath)
    {
        var httpUri = new Uri(baseUrl.TrimEnd('/') + "/");
        var scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var path = webSocketPath.StartsWith('/') ? webSocketPath : $"/{webSocketPath}";
        return new Uri($"{scheme}://{httpUri.Authority}{path}");
    }
}

/// <summary>
/// 将 <see cref="IGatewayConnection"/> 适配为 <see cref="IGatewayClient"/>
/// </summary>
public sealed class WebSocketGatewayClientAdapter : IGatewayClient, IAsyncDisposable
{
    private readonly WebSocketGatewayClient _connection;

    public WebSocketGatewayClientAdapter(WebSocketGatewayClient connection)
    {
        _connection = connection;
    }

    public async IAsyncEnumerable<GatewayEvent> ChatAsync(
        GatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestId = await _connection.SendChatAsync(request, cancellationToken).ConfigureAwait(false);

        await foreach (var inbound in _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (inbound.Type)
            {
                case GatewayWsFrameType.ChatEvent when inbound.Event != null:
                    yield return inbound.Event;
                    break;

                case GatewayWsFrameType.ChatComplete when inbound.Id == requestId:
                    yield break;

                case GatewayWsFrameType.ChatError when inbound.Id == requestId:
                    throw new InvalidOperationException(inbound.Error?.Message ?? "Chat failed");
            }
        }
    }

    public Task StopChatAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _connection.StopChatAsync(sessionId, cancellationToken);

    public Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        bool allow,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        _connection.RespondPermissionAsync(sessionId, permissionId, allow, reason, cancellationToken);

    public Task<IReadOnlyList<GatewayPendingPermission>> GetPendingPermissionsAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GatewayPendingPermission>>([]);

    public Task<GatewaySessionResetResult> ResetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _connection.ResetSessionAsync(sessionId, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
