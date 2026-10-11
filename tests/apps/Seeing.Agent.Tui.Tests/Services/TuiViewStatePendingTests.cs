using System.Collections.Immutable;
using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 排队中入站 user 消息的视图态回归：未消费消息不进主块列表，单独投影到排队区；
/// 消费（提升）后移入主块列表。
/// </summary>
public sealed class TuiViewStatePendingTests
{
    private const string Session = "s";

    private static SessionMessage PendingUser(string id, string text, bool consumed = false)
        => new()
        {
            Id = id,
            Role = MessageRole.User,
            Content = text,
            Metadata = ImmutableDictionary<string, object>.Empty
                .SetItem(InboxMetadataKeys.Presentation, "user")
                .SetItem(InboxMetadataKeys.Consumed, consumed),
        };

    [Fact]
    public void ResetFromSession_UnconsumedUser_ShouldGoToPendingNotBlocks()
    {
        var session = SessionData.Create();
        session.AddMessage(PendingUser("m1", "排队消息"));

        var state = new TuiViewState { SessionId = Session };
        state.ResetFromSession(session);

        state.PendingMessages.Should().ContainSingle().Which.Text.Should().Be("排队消息");
        state.PendingInputCount.Should().Be(1);
        state.Blocks.Should().NotContain(b => b.Kind == TuiBlockKind.User);
    }

    [Fact]
    public void ResetFromSession_ConsumedUser_ShouldGoToBlocksNotPending()
    {
        var session = SessionData.Create();
        session.AddMessage(PendingUser("m1", "已消费", consumed: true));

        var state = new TuiViewState { SessionId = Session };
        state.ResetFromSession(session);

        state.PendingMessages.Should().BeEmpty();
        state.PendingInputCount.Should().Be(0);
        state.Blocks.Should().ContainSingle(b => b.Kind == TuiBlockKind.User);
    }

    [Fact]
    public void ResetFromSession_NonInboxUser_ShouldGoToBlocks()
    {
        // 无 inbox 元数据的普通 user 消息（如历史会话）不进排队区。
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "普通" });

        var state = new TuiViewState { SessionId = Session };
        state.ResetFromSession(session);

        state.PendingMessages.Should().BeEmpty();
        state.Blocks.Should().ContainSingle(b => b.Kind == TuiBlockKind.User);
    }

    [Fact]
    public void AddPending_ShouldDedupeById()
    {
        var state = new TuiViewState { SessionId = Session };

        state.AddPending(new TuiPendingMessage { Id = "a", Text = "1" }).Should().BeTrue();
        state.AddPending(new TuiPendingMessage { Id = "a", Text = "1-again" }).Should().BeFalse();

        state.PendingMessages.Should().ContainSingle();
    }

    [Fact]
    public void PromoteAllPending_ShouldMoveToUserBlocksAndClearPending()
    {
        var state = new TuiViewState { SessionId = Session };
        state.AddPending(new TuiPendingMessage { Id = "a", Text = "第一" });
        state.AddPending(new TuiPendingMessage { Id = "b", Text = "第二" });

        state.PromoteAllPending().Should().BeTrue();

        state.PendingMessages.Should().BeEmpty();
        state.PendingInputCount.Should().Be(0);
        state.Blocks.Where(b => b.Kind == TuiBlockKind.User)
            .Select(b => b.Text)
            .Should().Equal("第一", "第二");
    }

    [Fact]
    public void PromoteAllPending_WhenEmpty_ShouldBeNoOp()
        => new TuiViewState { SessionId = Session }.PromoteAllPending().Should().BeFalse();
}
