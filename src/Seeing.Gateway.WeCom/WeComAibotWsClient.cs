using System.Text.Json.Serialization;
using Seeing.Gateway.WeCom.Connection;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微 AI Bot WebSocket 客户端 façade：出站 API 委托给 <see cref="WeComConnectionManager"/>。
/// </summary>
public sealed class WeComAibotWsClient : IAsyncDisposable
{
    private readonly WeComConnectionManager _connection;

    public WeComAibotWsClient(WeComConnectionManager connection)
    {
        _connection = connection;
    }

    public WeComConnectionManager Connection => _connection;

    public bool IsConnected => _connection.IsConnected;

    public long ConnectionEpoch => _connection.ConnectionEpoch;

    public event Func<WeComIncomingContext, CancellationToken, Task>? OnMessage
    {
        add => _connection.OnMessage += value;
        remove => _connection.OnMessage -= value;
    }

    public event Func<WeComWsFrame, CancellationToken, Task>? OnEvent
    {
        add => _connection.OnEvent += value;
        remove => _connection.OnEvent -= value;
    }

    public event EventHandler<WeComConnectionChangedEventArgs>? ConnectionChanged
    {
        add => _connection.ConnectionChanged += value;
        remove => _connection.ConnectionChanged -= value;
    }

    public Task ConnectAsync(WeComWsClientOptions options, CancellationToken cancellationToken = default) =>
        _connection.StartAsync(options, cancellationToken);

    public Task ReplyStreamAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        bool finish,
        CancellationToken cancellationToken = default) =>
        _connection.Outbound.ReplyStreamAsync(
            requestFrame,
            streamId,
            content,
            finish,
            _connection.ConnectionEpoch,
            cancellationToken);

    public Task ReplyWelcomeAsync(
        WeComWsFrame eventFrame,
        string text,
        CancellationToken cancellationToken = default) =>
        _connection.Outbound.ReplyWelcomeAsync(
            eventFrame,
            text,
            _connection.ConnectionEpoch,
            cancellationToken);

    public Task ReplyTemplateCardAsync(
        WeComWsFrame requestFrame,
        WeComTemplateCardRespondBody body,
        CancellationToken cancellationToken = default) =>
        _connection.Outbound.ReplyTemplateCardAsync(
            requestFrame,
            body,
            _connection.ConnectionEpoch,
            cancellationToken);

    public Task ReplyUpdateTemplateCardAsync(
        WeComWsFrame eventFrame,
        WeComTemplateCardUpdateBody body,
        CancellationToken cancellationToken = default) =>
        _connection.Outbound.ReplyUpdateTemplateCardAsync(
            eventFrame,
            body,
            _connection.ConnectionEpoch,
            cancellationToken);

    public Task SendProcessingIndicatorAsync(
        WeComWsFrame requestFrame,
        string streamId,
        CancellationToken cancellationToken = default) =>
        _connection.Outbound.SendProcessingIndicatorAsync(
            requestFrame,
            streamId,
            _connection.ConnectionEpoch,
            cancellationToken);

    public static string GenerateStreamId() => $"stream_{Guid.NewGuid():N}";

    public static string GenerateReqId(string prefix = "req") =>
        WeComConnectionManager.GenerateReqId(prefix);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
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
