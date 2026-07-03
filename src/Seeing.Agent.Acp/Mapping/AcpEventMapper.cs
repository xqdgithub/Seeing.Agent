using Acp.Types;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Acp.Mapping;

/// <summary>
/// SessionUpdate → IMessageEvent 映射。
/// </summary>
public sealed class AcpEventMapper
{
    public IEnumerable<IMessageEvent> Map(SessionUpdate update, string seeingSessionId, string? loopId = null)
    {
        return update switch
        {
            AgentMessageChunk chunk => MapTextDelta(chunk.Content, seeingSessionId, loopId, reasoning: false),
            AgentThoughtChunk thought => MapTextDelta(thought.Content, seeingSessionId, loopId, reasoning: true),
            UserMessageChunk user => MapTextDelta(user.Content, seeingSessionId, loopId, reasoning: false),
            ToolCallStart start => new[]
            {
                new ToolCallEvent
                {
                    SessionId = seeingSessionId,
                    LoopId = loopId,
                    Type = MessageEventType.ToolCallPending,
                    ToolCallId = start.ToolCallId,
                    ToolName = string.IsNullOrWhiteSpace(start.ToolName) ? start.Title : start.ToolName,
                    Arguments = start.Input,
                    Status = ToolCallStatus.Pending,
                    Title = start.Title
                }
            },
            ToolCallProgress progress => new[]
            {
                new ToolCallEvent
                {
                    SessionId = seeingSessionId,
                    LoopId = loopId,
                    Type = MapToolProgressType(progress.Status),
                    ToolCallId = progress.ToolCallId,
                    ToolName = progress.ToolName,
                    Arguments = progress.Input,
                    Status = MapToolStatus(progress.Status),
                    Output = ExtractProgressOutput(progress)
                }
            },
            AgentPlanUpdate => Array.Empty<IMessageEvent>(),
            SessionInfoUpdate => Array.Empty<IMessageEvent>(),
            UsageUpdate usage => MapUsage(usage, seeingSessionId, loopId),
            CurrentModeUpdate => Array.Empty<IMessageEvent>(),
            ConfigOptionUpdate => Array.Empty<IMessageEvent>(),
            AvailableCommandsUpdate => Array.Empty<IMessageEvent>(),
            UnknownSessionUpdate => Array.Empty<IMessageEvent>(),
            _ => Array.Empty<IMessageEvent>()
        };
    }

    public LoopStartEvent CreateLoopStart(string sessionId, string? loopId, string? userInput) =>
        new()
        {
            SessionId = sessionId,
            LoopId = loopId ?? Guid.NewGuid().ToString("N"),
            UserInput = userInput
        };

    public StreamStartEvent CreateStreamStart(string sessionId, string? loopId, int step = 0) =>
        new()
        {
            SessionId = sessionId,
            LoopId = loopId,
            Step = step
        };

    public LoopCompleteEvent CreateLoopComplete(
        string sessionId,
        string loopId,
        bool success,
        TimeSpan duration,
        string? error = null) =>
        new()
        {
            SessionId = sessionId,
            LoopId = loopId,
            Success = success,
            Duration = duration,
            Error = error
        };

    private static IEnumerable<IMessageEvent> MapTextDelta(
        ContentBlock? content,
        string sessionId,
        string? loopId,
        bool reasoning)
    {
        if (content is not TextContentBlock text || string.IsNullOrEmpty(text.Text))
            return Array.Empty<IMessageEvent>();

        return new IMessageEvent[]
        {
            new StreamDeltaEvent
            {
                SessionId = sessionId,
                LoopId = loopId,
                ContentDelta = reasoning ? null : text.Text,
                ReasoningDelta = reasoning ? text.Text : null
            }
        };
    }

    private static MessageEventType MapToolProgressType(string status) =>
        status switch
        {
            "completed" or "failed" => MessageEventType.ToolCallComplete,
            "running" or "in_progress" => MessageEventType.ToolCallRunning,
            _ => MessageEventType.ToolCallPending
        };

    private static ToolCallStatus MapToolStatus(string status) =>
        status switch
        {
            "completed" => ToolCallStatus.Success,
            "failed" => ToolCallStatus.Failed,
            "running" or "in_progress" => ToolCallStatus.Running,
            _ => ToolCallStatus.Pending
        };

    private static string? ExtractProgressOutput(ToolCallProgress progress)
    {
        if (progress.Content == null)
            return null;

        var parts = progress.Content
            .Select(item => item.Content is TextContentBlock text ? text.Text : item.Type)
            .Where(s => !string.IsNullOrWhiteSpace(s));

        return string.Join("\n", parts);
    }

    private static IEnumerable<IMessageEvent> MapUsage(UsageUpdate usage, string sessionId, string? loopId) =>
        new IMessageEvent[]
        {
            new StreamDeltaEvent
            {
                SessionId = sessionId,
                LoopId = loopId,
                Usage = new TokenUsage
                {
                    OutputTokens = usage.Used
                }
            }
        };

}
