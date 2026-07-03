using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Acp.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Core.Events;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// 将 SessionUpdate 映射为 <see cref="IMessageEvent"/> 并通过 Channel 产出。
/// </summary>
public sealed class EventYieldingSink : IAcpUpdateSink
{
    private readonly AcpEventMapper _mapper;
    private readonly string _seeingSessionId;
    private readonly string? _loopId;
    private readonly ILogger _logger;
    private readonly Channel<IMessageEvent> _channel;
    private int _updateCount;

    public EventYieldingSink(
        AcpEventMapper mapper,
        string seeingSessionId,
        ILogger? logger = null,
        string? loopId = null)
    {
        _mapper = mapper;
        _seeingSessionId = seeingSessionId;
        _loopId = loopId;
        _logger = logger ?? NullLogger.Instance;
        _channel = Channel.CreateUnbounded<IMessageEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task OnSessionUpdateAsync(string acpSessionId, SessionUpdate update, CancellationToken cancellationToken = default)
    {
        var kind = AcpSessionUpdateLogging.Describe(update);
        _logger.Log(
            AcpSessionUpdateLogging.GetLogLevel(update),
            "ACP session update #{Count} acpSession={AcpSessionId} seeingSession={SeeingSessionId} loop={LoopId} kind={Kind}",
            Interlocked.Increment(ref _updateCount),
            acpSessionId,
            _seeingSessionId,
            _loopId,
            kind);

        var mappedCount = 0;
        foreach (var evt in _mapper.Map(update, _seeingSessionId, _loopId))
        {
            _channel.Writer.TryWrite(evt);
            mappedCount++;
            _logger.LogDebug(
                "ACP mapped update kind={Kind} -> event={EventType}",
                kind,
                evt.Type);
        }

        if (mappedCount == 0)
        {
            _logger.LogDebug("ACP update kind={Kind} produced no mapped events", kind);
        }

        return Task.CompletedTask;
    }

    public Task PublishAsync(IMessageEvent messageEvent, CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryWrite(messageEvent);
        _logger.LogDebug(
            "ACP published event={EventType} session={SessionId} loop={LoopId}",
            messageEvent.Type,
            _seeingSessionId,
            _loopId);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<IMessageEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var evt))
                yield return evt;
        }
    }

    public void Complete()
    {
        if (_channel.Writer.TryComplete())
        {
            _logger.LogInformation(
                "ACP event channel completed session={SessionId} loop={LoopId} updates={UpdateCount}",
                _seeingSessionId,
                _loopId,
                _updateCount);
        }
    }
}
