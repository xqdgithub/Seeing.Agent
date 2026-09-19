using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Xunit;

namespace Seeing.Agent.Agents.BuiltIn.Tests;

public class AgentsBuiltInModuleTests
{
    [Fact]
    public void Module_Id_IsAgentsBuiltin()
    {
        var module = new AgentsBuiltInModule();
        module.Id.Should().Be("agents.builtin");
    }

    [Fact]
    public void ProvidedSeams_ContainsAgents()
    {
        var module = new AgentsBuiltInModule();
        module.ProvidedSeams.Should().Contain("agents");
    }

    [Fact]
    public void Explore_DeniedTools_ContainsTask()
    {
        var explore = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "explore");
        explore.DeniedTools.Should().Contain("task");
    }

    [Fact]
    public void General_DeniedTools_ContainsTask()
    {
        var general = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "general");
        general.DeniedTools.Should().Contain("task");
    }

    [Fact]
    public void Build_HasEmptyAllowedAndDeniedTools()
    {
        var build = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "build");
        build.AllowedTools.Should().BeEmpty();
        build.DeniedTools.Should().BeEmpty();
    }

    [Fact]
    public void Plan_AllowedTools_IsWhitelist()
    {
        var plan = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "plan");
        plan.AllowedTools.Should().NotBeEmpty();
        plan.AllowedTools.Should().Contain(["read", "grep", "glob", "task", "todowrite", "skill"]);
    }

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

    // explore 应默认放开只读工具（Git 只读 + 记忆只读），以便子代理真正探索仓库。
    [Theory]
    [InlineData("git_status")]
    [InlineData("git_diff")]
    [InlineData("git_log")]
    [InlineData("memory_search")]
    [InlineData("memory_read")]
    public void Explore_ShouldAllowReadOnlyTools(string toolId)
    {
        var explore = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "explore");

        explore.PermissionRules.Should().Contain(r =>
            r.Kind == PermissionKind.Tool &&
            r.Pattern == toolId &&
            r.Effect == PermissionEffect.Allow);
    }

    // 只读放开不得扩及写工具（git_commit / memory_write 仍不得被 Allow）。
    [Theory]
    [InlineData("git_commit")]
    [InlineData("memory_write")]
    public void Explore_ShouldNotAllowWriteTools(string toolId)
    {
        var explore = BuiltInAgents.GetBuiltInAgents().First(a => a.Name == "explore");

        explore.PermissionRules.Should().NotContain(r =>
            r.Kind == PermissionKind.Tool &&
            r.Pattern == toolId &&
            r.Effect == PermissionEffect.Allow);
    }
}
