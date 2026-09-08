namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微流式回复发送抽象（便于测试注入）
/// </summary>
internal interface IWeComStreamSender
{
    Task SendProcessingIndicatorAsync(
        WeComWsFrame requestFrame,
        string streamId,
        long connectionEpoch,
        CancellationToken cancellationToken);

    Task ReplyStreamAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        bool finish,
        long connectionEpoch,
        CancellationToken cancellationToken,
        Connection.WeComOutboundPriority priority = Connection.WeComOutboundPriority.ContentDelta);
}

internal sealed class WeComAibotStreamSender(WeComAibotWsClient client) : IWeComStreamSender
{
    public Task SendProcessingIndicatorAsync(
        WeComWsFrame requestFrame,
        string streamId,
        long connectionEpoch,
        CancellationToken cancellationToken) =>
        client.Connection.Outbound.SendProcessingIndicatorAsync(
            requestFrame,
            streamId,
            client.ConnectionEpoch,
            cancellationToken);

    public Task ReplyStreamAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        bool finish,
        long connectionEpoch,
        CancellationToken cancellationToken,
        Connection.WeComOutboundPriority priority = Connection.WeComOutboundPriority.ContentDelta) =>
        client.Connection.Outbound.ReplyStreamAsync(
            requestFrame,
            streamId,
            content,
            finish,
            client.ConnectionEpoch,
            cancellationToken,
            priority);
}
