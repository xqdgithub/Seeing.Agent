using FluentAssertions;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionWindowContextCapabilityTests
{
    [Fact]
    public void ApplyCapabilities_Child_ShouldBeReadOnlyDetachableAndRestricted()
    {
        var ctx = new SessionWindowContext();

        ctx.ApplyCapabilities(isChild: true, hasParent: true);

        ctx.IsChild.Should().BeTrue();
        ctx.IsReadOnly.Should().BeTrue();
        ctx.AllowAutoApproveChange.Should().BeFalse();
        ctx.ShowScenarioBadge.Should().BeFalse();
        ctx.ShowSubAgentBadge.Should().BeTrue();
        ctx.ShowAcpModeSelector.Should().BeFalse();
        ctx.ShowModelSelector.Should().BeFalse();
        ctx.ShowReturnToParent.Should().BeTrue();
        ctx.CanRename.Should().BeFalse();
        ctx.CanDetach.Should().BeTrue();
        ctx.CanBranch.Should().BeFalse();
        ctx.CanEditOutboundBinding.Should().BeFalse();
        ctx.CanClear.Should().BeFalse();
        ctx.CanCreateSession.Should().BeFalse();
    }

    [Fact]
    public void ApplyCapabilities_NonChild_ShouldBeFullyEditableWithoutSubAgentMarkers()
    {
        var ctx = new SessionWindowContext();

        ctx.ApplyCapabilities(isChild: false, hasParent: false);

        ctx.IsChild.Should().BeFalse();
        ctx.IsReadOnly.Should().BeFalse();
        ctx.AllowAutoApproveChange.Should().BeTrue();
        ctx.ShowScenarioBadge.Should().BeTrue();
        ctx.ShowSubAgentBadge.Should().BeFalse();
        ctx.ShowAcpModeSelector.Should().BeTrue();
        ctx.ShowModelSelector.Should().BeTrue();
        ctx.ShowReturnToParent.Should().BeFalse();
        ctx.CanRename.Should().BeTrue();
        ctx.CanDetach.Should().BeFalse();
        ctx.CanBranch.Should().BeTrue();
        ctx.CanEditOutboundBinding.Should().BeTrue();
        ctx.CanClear.Should().BeTrue();
        ctx.CanCreateSession.Should().BeTrue();
    }

    [Fact]
    public void ApplyCapabilities_ChildWithoutParent_ShouldNotShowReturnToParent()
    {
        var ctx = new SessionWindowContext();

        ctx.ApplyCapabilities(isChild: true, hasParent: false);

        ctx.IsChild.Should().BeTrue();
        ctx.ShowReturnToParent.Should().BeFalse();
    }
}
