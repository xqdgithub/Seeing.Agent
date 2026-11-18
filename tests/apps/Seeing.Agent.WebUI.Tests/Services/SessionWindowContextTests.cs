using FluentAssertions;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionWindowContextTests
{
    [Fact]
    public void Child_Not_Taken_Over_Should_Be_Read_Only_And_Offer_Takeover()
    {
        var ctx = new SessionWindowContext();
        ctx.ApplyCapabilities(isChild: true, hasParent: true, owner: SessionOwner.Parent);

        ctx.CanEdit.Should().BeFalse();
        ctx.CanTakeOver.Should().BeTrue();
        ctx.ShowModelSelector.Should().BeFalse();
        ctx.CanRename.Should().BeFalse();
        ctx.CanClear.Should().BeFalse();
        ctx.CanBranch.Should().BeFalse();
        ctx.CanCreateSession.Should().BeFalse();
        ctx.CanEditOutboundBinding.Should().BeFalse();
        ctx.AllowAutoApproveChange.Should().BeFalse();
        ctx.ShowAcpModeSelector.Should().BeFalse();
    }

    [Fact]
    public void Child_Taken_Over_Should_Be_Editable()
    {
        var ctx = new SessionWindowContext();
        ctx.ApplyCapabilities(true, true, SessionOwner.Human);

        ctx.CanEdit.Should().BeTrue();
        ctx.CanTakeOver.Should().BeFalse();
        ctx.ShowModelSelector.Should().BeTrue();
        ctx.CanRename.Should().BeTrue();
        ctx.CanClear.Should().BeTrue();
    }

    [Fact]
    public void Root_Session_Should_Always_Be_Editable()
    {
        var ctx = new SessionWindowContext();
        ctx.ApplyCapabilities(false, false, SessionOwner.Self);

        ctx.CanEdit.Should().BeTrue();
        ctx.CanTakeOver.Should().BeFalse();
    }
}
