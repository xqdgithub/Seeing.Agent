using FluentAssertions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class SessionPermissionMapperTests
{
    [Fact]
    public void ApplySnapshot_ShouldMergeDenyIntoPolicy()
    {
        var basePolicy = new AgentPermissionPolicy
        {
            AgentName = "explore",
            Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0) },
            DefaultEffect = PermissionEffect.Allow
        };

        var snapshot = new List<SessionPermissionRule>
        {
            new()
            {
                Kind = nameof(PermissionKind.Tool),
                Pattern = "task",
                Effect = nameof(PermissionEffect.Deny),
                Priority = 100
            }
        };

        var merged = SessionPermissionMapper.ApplySnapshot(basePolicy, snapshot);

        merged.DeniedTools.Should().Contain("task");
        merged.Rules.Should().Contain(r =>
            r.Pattern == "task" && r.Effect == PermissionEffect.Deny);
    }
}
