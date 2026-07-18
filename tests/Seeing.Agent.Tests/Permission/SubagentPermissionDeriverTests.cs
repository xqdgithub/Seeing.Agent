using FluentAssertions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class SubagentPermissionDeriverTests
{
    [Fact]
    public void Derive_PlanParentDenyEdit_ShouldForwardEditDeny()
    {
        var parent = new AgentDefinition
        {
            Name = "plan",
            PermissionRules =
            {
                PermissionRuleEntry.Deny(PermissionKind.Tool, "edit", 100),
                PermissionRuleEntry.Deny(PermissionKind.Tool, "write", 100),
            }
        };
        var sub = new AgentDefinition
        {
            Name = "explore",
            PermissionRules = { PermissionRuleEntry.Allow(PermissionKind.Tool, "read", 0) }
        };

        var snap = SubagentPermissionDeriver.Derive(Array.Empty<SessionPermissionRule>(), parent, sub);

        snap.Should().Contain(r => r.Pattern == "edit" && r.Effect == "Deny");
        snap.Should().Contain(r => r.Pattern == "write" && r.Effect == "Deny");
        snap.Should().Contain(r => r.Pattern == "task" && r.Effect == "Deny");
        snap.Should().Contain(r => r.Pattern == "todowrite" && r.Effect == "Deny");
    }

    [Fact]
    public void Derive_SubagentAllowsTask_ShouldNotAddTaskDeny()
    {
        var sub = new AgentDefinition
        {
            Name = "general",
            PermissionRules =
            {
                PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0),
                PermissionRuleEntry.Allow(PermissionKind.Tool, "task", 0),
            }
        };

        var snap = SubagentPermissionDeriver.Derive(Array.Empty<SessionPermissionRule>(), null, sub);

        snap.Should().NotContain(r => r.Pattern == "task" && r.Effect == "Deny");
        snap.Should().NotContain(r => r.Pattern == "todowrite" && r.Effect == "Deny");
    }

    [Fact]
    public void Derive_ParentSessionDenyBash_ShouldForward()
    {
        var parentSession = new[]
        {
            new SessionPermissionRule { Kind = "Tool", Pattern = "bash", Effect = "Deny", Priority = 50 }
        };
        var sub = new AgentDefinition
        {
            Name = "explore",
            PermissionRules = { PermissionRuleEntry.Allow(PermissionKind.Tool, "read", 0) }
        };

        var snap = SubagentPermissionDeriver.Derive(parentSession, null, sub);

        snap.Should().Contain(r => r.Pattern == "bash" && r.Effect == "Deny");
    }
}
