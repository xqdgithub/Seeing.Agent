using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Seeing.Gateway.WeCom.Connection;

/// <summary>
/// 绑定 connection epoch 的出站通道；epoch 过期时 fail-fast。
/// </summary>
public sealed class WeComOutboundChannel
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly WeComOutboundGovernor _governor;
    private readonly ILogger<WeComOutboundChannel> _logger;

    private WeComWebSocketTransport? _transport;
    private long _epoch;

    public WeComOutboundChannel(
        WeComOutboundGovernor governor,
        ILogger<WeComOutboundChannel> logger)
    {
        _governor = governor;
        _logger = logger;
    }

    public long Epoch => Volatile.Read(ref _epoch);

    public bool IsReady => _transport?.IsOpen == true && _epoch > 0;

    internal void Bind(WeComWebSocketTransport transport, long epoch)
    {
        _transport = transport;
        Volatile.Write(ref _epoch, epoch);
    }

    internal void Unbind()
    {
        _transport = null;
        Volatile.Write(ref _epoch, 0);
    }

    public Task ReplyStreamAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        bool finish,
        long epoch,
        CancellationToken cancellationToken = default,
        WeComOutboundPriority priority = WeComOutboundPriority.ContentDelta)
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

        var effectivePriority = finish ? WeComOutboundPriority.Finish : priority;

        return SendCommandAsync(
            WeComWsCommands.RespondMsg,
            requestFrame.Headers?.ReqId,
            body,
            epoch,
            cancellationToken,
            effectivePriority);
    }

    public Task ReplyWelcomeAsync(
        WeComWsFrame eventFrame,
        string text,
        long epoch,
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
            epoch,
            cancellationToken,
            WeComOutboundPriority.Finish);
    }

    public Task ReplyTemplateCardAsync(
        WeComWsFrame requestFrame,
        WeComTemplateCardRespondBody body,
        long epoch,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            WeComWsCommands.RespondMsg,
            requestFrame.Headers?.ReqId,
            body,
            epoch,
            cancellationToken,
            WeComOutboundPriority.Finish);

    public Task ReplyUpdateTemplateCardAsync(
        WeComWsFrame eventFrame,
        WeComTemplateCardUpdateBody body,
        long epoch,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            WeComWsCommands.RespondUpdate,
            eventFrame.Headers?.ReqId,
            body,
            epoch,
            cancellationToken,
            WeComOutboundPriority.Finish);

    public Task SendProcessingIndicatorAsync(
        WeComWsFrame requestFrame,
        string streamId,
        long epoch,
        CancellationToken cancellationToken = default) =>
        ReplyStreamAsync(
            requestFrame,
            streamId,
            "🤔 Thinking...",
            finish: false,
            epoch,
            cancellationToken,
            WeComOutboundPriority.Keepalive);

    public async Task SendCommandAsync(
        string cmd,
        string? reqId,
        object body,
        long epoch,
        CancellationToken cancellationToken = default,
        WeComOutboundPriority priority = WeComOutboundPriority.ContentDelta)
    {
        EnsureEpoch(epoch);

        if (_transport == null || !_transport.IsOpen)
            throw new InvalidOperationException("WeCom WebSocket 未连接");

        await _governor.WaitForSlotAsync(priority, cancellationToken).ConfigureAwait(false);

        var frame = new WeComWsFrame
        {
            Cmd = cmd,
            Headers = new WeComWsHeaders { ReqId = reqId ?? WeComConnectionManager.GenerateReqId() },
            Body = JsonSerializer.SerializeToElement(body, WeComWsJson.Options)
        };

        var json = JsonSerializer.Serialize(frame, WeComWsJson.Options);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureEpoch(epoch);
            await _transport.SendTextAsync(json, cancellationToken).ConfigureAwait(false);
            _governor.RecordSend(priority);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void EnsureEpoch(long epoch)
    {
        var current = Volatile.Read(ref _epoch);
        if (current == 0 || current != epoch)
            throw new WeComConnectionEpochException(epoch, current);
    }
}
