using FluentAssertions;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Sessions;

public class SessionControlTests
{
    [Fact]
    public void GetControl_WithoutMetadata_ShouldReturnParent()
    {
        var session = new SessionData { Id = "s1" };
        session.GetControl().Owner.Should().Be(SessionOwner.Parent);
    }

    [Fact]
    public void SetOwner_Human_ShouldPersistAndRoundTrip()
    {
        var session = new SessionData { Id = "s1" };
        session.SetOwner(SessionOwner.Human, "tester");
        session.GetControl().Owner.Should().Be(SessionOwner.Human);
        session.Metadata[SessionControlKeys.TakenOverBy].Should().Be("tester");
        session.Metadata[SessionControlKeys.Owner].Should().Be("human");
    }

    [Fact]
    public void StopReasonKeys_ShouldBeStable()
    {
        SessionControlKeys.StopReason.Should().Be("control.stop_reason");
        SessionControlKeys.StopReasonCascadedFromParent.Should().Be("cascaded_from_parent_stop");
    }
}
