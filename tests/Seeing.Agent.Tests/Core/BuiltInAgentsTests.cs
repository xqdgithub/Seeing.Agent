using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core.BuiltInAgents;
using Seeing.Agent.Abstractions.Permissions;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// 内置 Agent 权限规则测试
/// </summary>
public class BuiltInAgentsTests
{
    [Fact]
    public void Explore_ShouldAllowTodoWrite()
    {
        var explore = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "explore");

        explore.PermissionRules.Should().Contain(r =>
            r.Kind == PermissionKind.Tool &&
            r.Pattern == "todowrite" &&
            r.Effect == PermissionEffect.Allow);
        explore.PermissionRules.Should().NotContain(r =>
            r.Kind == PermissionKind.Tool &&
            r.Pattern == "todowrite" &&
            r.Effect == PermissionEffect.Deny);
    }

    [Fact]
    public void Explore_ShouldDenyTask_ToPreventNestedDelegation()
    {
        var explore = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "explore");

        explore.PermissionRules.Should().Contain(r =>
            r.Kind == PermissionKind.Tool &&
            r.Pattern == "task" &&
            r.Effect == PermissionEffect.Deny);
    }
}
