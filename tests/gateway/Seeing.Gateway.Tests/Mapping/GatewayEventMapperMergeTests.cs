using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.Tests.Mapping;

public class GatewayEventMapperMergeTests
{
    [Fact]
    public void TryMap_LoopCancelled_MapsToCancelled()
    {
        var evt = new LoopCancelledEvent { SessionId = "s", LoopId = "l", Reason = "user" };
        GatewayEventMapper.TryMap(evt, out var ge).Should().BeTrue();
        ge!.SourceType.Should().Be(MessageEventType.LoopCancelled);
        ge.Status.Should().Be(GatewayEventStatus.Cancelled);
        ge.Data!.CancelReason.Should().Be("user");
    }

    [Fact]
    public void TryMap_CommandResult_Navigate_SkillContent_AreMapped()
    {
        GatewayEventMapper.TryMap(
            new CommandResultEvent { SessionId = "s", CommandName = "cmd", Success = true, Message = "m" },
            out var a).Should().BeTrue();
        a!.Object.Should().Be(GatewayEventObject.Message);
        a.Data!.Text.Should().Be("m");
        a.Data.Success.Should().BeTrue();
        a.SourceType.Should().Be(MessageEventType.CommandResult);

        GatewayEventMapper.TryMap(new NavigateEvent { SessionId = "s", Target = "/x" }, out var b).Should().BeTrue();
        b!.Object.Should().Be(GatewayEventObject.Message);
        b.Data!.Text.Should().Be("/x");
        b.SourceType.Should().Be(MessageEventType.Navigate);

        GatewayEventMapper.TryMap(
            new SkillContentEvent { SessionId = "s", OriginalContent = "o", ExpandedContent = "c" },
            out var c).Should().BeTrue();
        c!.Object.Should().Be(GatewayEventObject.Content);
        c.Data!.Text.Should().Be("c");
        c.SourceType.Should().Be(MessageEventType.SkillContent);
    }

    [Fact]
    public void TryMap_UnknownEvent_ReturnsFalse()
    {
        GatewayEventMapper.TryMap(new SessionUpdatedEvent { SessionId = "s", Session = null! }, out _).Should().BeFalse();
    }

    [Fact]
    public void TryMap_LoopComplete_SourceTypeUsesEventTypeConstant()
    {
        var evt = new LoopCompleteEvent { SessionId = "s", LoopId = "l", Success = true };
        GatewayEventMapper.TryMap(evt, out var ge).Should().BeTrue();
        ge!.SourceType.Should().Be(MessageEventType.LoopComplete);
    }
}
