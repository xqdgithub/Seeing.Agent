using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Execution;

namespace Seeing.Agent.App.Events;

/// <summary>
/// 压缩进度事件适配器：把主库 <see cref="ICompactionEventSink"/> 的进度发布到 <see cref="IExecutionEventPublisher"/> 事件流，
/// 使 WebUI 等订阅方实时收到压缩进度。
/// </summary>
public sealed class CompactionEventSink : ICompactionEventSink
{
    private readonly IExecutionEventPublisher _publisher;

    public CompactionEventSink(IExecutionEventPublisher publisher)
    {
        _publisher = publisher;
    }

    public void PublishDelta(string sessionId, string stage, string? contentDelta = null, string? reasoningDelta = null)
    {
        _publisher.Publish(sessionId, new CompactionDeltaEvent
        {
            SessionId = sessionId,
            Stage = stage,
            ContentDelta = contentDelta,
            ReasoningDelta = reasoningDelta
        });
    }
}