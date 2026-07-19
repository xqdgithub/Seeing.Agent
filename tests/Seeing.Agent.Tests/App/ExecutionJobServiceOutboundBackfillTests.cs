using FluentAssertions;
using Seeing.Agent.App.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ExecutionJobServiceOutboundBackfillTests
{
    [Fact]
    public void TryBackfillSessionOutbound_EmptyFields_ShouldFillFromInbound()
    {
        var session = SessionData.Create();
        session.ChannelId = null;
        session.UserId = null;

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "  qq  ", "  u1  ");

        changed.Should().BeTrue();
        session.ChannelId.Should().Be("qq");
        session.UserId.Should().Be("u1");
    }

    [Fact]
    public void TryBackfillSessionOutbound_ExistingFields_ShouldNotOverwrite()
    {
        var session = SessionData.Create();
        session.ChannelId = "wecom";
        session.UserId = "existing";

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "qq", "u1");

        changed.Should().BeFalse();
        session.ChannelId.Should().Be("wecom");
        session.UserId.Should().Be("existing");
    }

    [Fact]
    public void TryBackfillSessionOutbound_WhitespaceSessionFields_ShouldFill()
    {
        var session = SessionData.Create();
        session.ChannelId = "   ";
        session.UserId = "";

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "qq", "u1");

        changed.Should().BeTrue();
        session.ChannelId.Should().Be("qq");
        session.UserId.Should().Be("u1");
    }

    [Fact]
    public void TryBackfillSessionOutbound_BlankInbound_ShouldNotChange()
    {
        var session = SessionData.Create();
        session.ChannelId = null;
        session.UserId = null;

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "  ", null);

        changed.Should().BeFalse();
        session.ChannelId.Should().BeNull();
        session.UserId.Should().BeNull();
    }

    [Fact]
    public void TryBackfillSessionOutbound_OnlyChannelEmpty_ShouldFillChannelOnly()
    {
        var session = SessionData.Create();
        session.ChannelId = null;
        session.UserId = "keep";

        var changed = ExecutionJobService.TryBackfillSessionOutbound(session, "qq", "ignored");

        changed.Should().BeTrue();
        session.ChannelId.Should().Be("qq");
        session.UserId.Should().Be("keep");
    }
}
