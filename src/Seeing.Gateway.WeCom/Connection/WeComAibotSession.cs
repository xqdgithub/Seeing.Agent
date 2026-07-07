using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Seeing.Gateway.WeCom.Connection;

/// <summary>
/// 单次连接 epoch 内的 subscribe 握手（每个 epoch 仅调用一次）。
/// </summary>
public sealed class WeComAibotSession
{
    private readonly ILogger<WeComAibotSession> _logger;

    public WeComAibotSession(ILogger<WeComAibotSession> logger)
    {
        _logger = logger;
    }

    public async Task SubscribeAsync(
        WeComWebSocketTransport transport,
        string botId,
        string secret,
        CancellationToken cancellationToken)
    {
        var reqId = WeComConnectionManager.GenerateReqId("sub");
        var frame = new WeComWsFrame
        {
            Cmd = WeComWsCommands.Subscribe,
            Headers = new WeComWsHeaders { ReqId = reqId },
            Body = JsonSerializer.SerializeToElement(
                new WeComSubscribeBody { BotId = botId, Secret = secret },
                WeComWsJson.Options)
        };

        var json = JsonSerializer.Serialize(frame, WeComWsJson.Options);
        await transport.SendTextAsync(json, cancellationToken).ConfigureAwait(false);

        var response = await transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
        if (response == null)
            throw new InvalidOperationException("WeCom 订阅无响应");

        if (response.ErrCode != 0)
            throw new InvalidOperationException($"WeCom 订阅失败: errcode={response.ErrCode}, errmsg={response.ErrMsg}");

        _logger.LogInformation("WeCom 订阅成功");
    }
}
