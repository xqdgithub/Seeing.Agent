using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// assistant 块 Key 规则：快照 <see cref="TuiViewState.ResetFromSession"/> 必须与
/// <see cref="TuiEventInterpreter"/> 一致——一个 (LoopId, Step) 只对应一个块；LoopId 为空时为 <c>asst:step{n}</c>。
/// </summary>
public sealed class TuiViewStateKeyTests
{
    private const string Session = "s";

    [Fact]
    public void ResetFromSession_NullLoopId_ShouldMatchInterpreterKey()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Id = "msg-1",
            Role = MessageRole.Assistant,
            Step = 2,
            Content = "hi",
        });

        var snapshotState = new TuiViewState { SessionId = Session };
        snapshotState.ResetFromSession(session);

        var interpreterState = new TuiViewState { SessionId = Session };
        var interpreter = new TuiEventInterpreter(interpreterState);
        interpreter.Apply(new StreamStartEvent { SessionId = Session, LoopId = null, Step = 2 });

        var key = TuiViewState.AssistantKey(null, 2, "step2");
        key.Should().Be("asst:step2");
        snapshotState.Find(key).Should().NotBeNull();
        interpreterState.Find(key).Should().NotBeNull();
        snapshotState.Find("asst:msg-1").Should().BeNull();
    }

    [Fact]
    public void ResetFromSession_WithLoopId_ShouldKeyByLoopAndStep()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Id = "m1",
            Role = MessageRole.Assistant,
            LoopId = "loopA",
            Step = 1,
            Content = "a",
        });

        var state = new TuiViewState { SessionId = Session };
        state.ResetFromSession(session);

        state.Find("loopA_step1").Should().NotBeNull();
        state.Find("asst:step1").Should().BeNull();
    }

    [Fact]
    public void ResetFromSession_SameLoopStep_ShouldProduceSingleAssistantBlock()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Id = "m1",
            Role = MessageRole.Assistant,
            LoopId = "loopA",
            Step = 0,
            Content = "first",
        });
        session.AddMessage(new SessionMessage
        {
            Id = "m2",
            Role = MessageRole.Assistant,
            LoopId = "loopA",
            Step = 0,
            Content = "second",
        });

        var state = new TuiViewState { SessionId = Session };
        state.ResetFromSession(session);

        state.Blocks.Where(b => b.Kind == TuiBlockKind.Assistant).Should().ContainSingle();
        state.Find("loopA_step0")!.Text.Should().Be("second");
    }

    [Fact]
    public void ResetFromSession_NullLoopIdSameStep_ShouldProduceSingleAssistantBlock()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Id = "m1",
            Role = MessageRole.Assistant,
            Step = 3,
            Content = "first",
        });
        session.AddMessage(new SessionMessage
        {
            Id = "m2",
            Role = MessageRole.Assistant,
            Step = 3,
            Content = "second",
        });

        var state = new TuiViewState { SessionId = Session };
        state.ResetFromSession(session);

        state.Blocks.Where(b => b.Kind == TuiBlockKind.Assistant).Should().ContainSingle();
        state.Find("asst:step3")!.Text.Should().Be("second");
    }
}
