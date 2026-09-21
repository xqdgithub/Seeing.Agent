using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

public class ChatEventTrackerStreamResetTests
{
    [Fact]
    public void StreamStart_RepeatedForSameStep_RemovesPreviousPartialAssistantMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "partial" });

        session.Messages.Where(m => m.Role == MessageRole.Assistant).Should().HaveCount(1);

        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "retry" });

        var assistants = session.Messages.Where(m => m.Role == MessageRole.Assistant).ToList();
        assistants.Should().HaveCount(1);
        assistants[0].Content.Should().Be("retry");
    }

    [Fact]
    public void StreamStart_NextStep_KeepsPreviousCompletedAssistantMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "first" });
        tracker.ApplyEvent(session, new StreamCompleteEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Message = new ChatMessage { Role = "assistant", Content = "first" }
        });

        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 1 });
        tracker.ApplyEvent(session, new StreamDeltaEvent { SessionId = session.Id, LoopId = "L1", ContentDelta = "second" });

        var assistants = session.Messages.Where(m => m.Role == MessageRole.Assistant).ToList();
        assistants.Should().HaveCount(2);
        assistants[0].Content.Should().Be("first");
        assistants[1].Content.Should().Be("second");
    }

    [Fact]
    public void ErrorEvent_DoesNotAddMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new ErrorEvent { SessionId = session.Id, Message = "网络连接错误" });

        session.Messages.Should().BeEmpty();
    }

    [Fact]
    public void LoopCancelledEvent_DoesNotAddMessage()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new LoopCancelledEvent { SessionId = session.Id, LoopId = "L1", Reason = "user" });

        session.Messages.Should().BeEmpty();
    }
}
