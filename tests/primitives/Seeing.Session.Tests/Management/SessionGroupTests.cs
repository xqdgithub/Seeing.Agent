using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests.Management
{
    public class SessionGroupTests
    {
        [Fact]
        public void SessionRelation_HandoffPredecessor_ShouldBeFour()
        {
            ((int)SessionRelation.HandoffPredecessor).Should().Be(4);
        }

        [Fact]
        public void ResolveActiveId_WhenActiveIsStale_ShouldFallbackToAnchor()
        {
            var group = new SessionGroup
            {
                AnchorSessionId = "a",
                ActiveSessionId = "ghost",
                Members = { new SessionGroupMember { SessionId = "a", IsAnchor = true, Relation = SessionRelation.None } }
            };
            group.ResolveActiveId().Should().Be("a");
        }

        [Fact]
        public void Clone_ShouldDeepCopyMembers()
        {
            var group = new SessionGroup { AnchorSessionId = "a", Members = { new SessionGroupMember { SessionId = "a", IsAnchor = true } } };
            var clone = group.Clone();
            clone.Members[0].SessionId = "x";
            group.Members[0].SessionId.Should().Be("a");
        }
    }
}
