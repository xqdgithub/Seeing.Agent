namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微流式回复发送抽象（便于测试注入）
/// </summary>
internal interface IWeComStreamSender
{
    Task SendProcessingIndicatorAsync(
        WeComWsFrame requestFrame,
        string streamId,
        CancellationToken cancellationToken);

    Task ReplyStreamAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        bool finish,
        CancellationToken cancellationToken);
}

internal sealed class WeComAibotStreamSender(WeComAibotWsClient client) : IWeComStreamSender
{
    public Task SendProcessingIndicatorAsync(
        WeComWsFrame requestFrame,
        string streamId,
        CancellationToken cancellationToken) =>
        client.SendProcessingIndicatorAsync(requestFrame, streamId, cancellationToken);

    public Task ReplyStreamAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        bool finish,
        CancellationToken cancellationToken) =>
        client.ReplyStreamAsync(requestFrame, streamId, content, finish, cancellationToken);
}
