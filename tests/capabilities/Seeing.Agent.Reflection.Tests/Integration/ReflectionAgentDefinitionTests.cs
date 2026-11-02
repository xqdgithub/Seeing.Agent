using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Integration;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Integration;

/// <summary>复盘 agent 的模式、工具白/黑名单与权限面。</summary>
public sealed class ReflectionAgentDefinitionTests
{
    private static AgentDefinition Create() => ReflectionAgentDefinition.Create(new ReflectionOptions());

    [Fact]
    public void Agent_Should_Use_Primary_And_Hidden()
    {
        var agent = Create();

        // Primary 让它不出现在「可委派列表」（按 Mode != Primary 排除），
        // IsHidden 让它不出现在「用户选择列表」（按 !IsHidden 排除）：两者组合才同时避开两个列表。
        agent.Mode.Should().Be(AgentMode.Primary);
        agent.IsHidden.Should().BeTrue();
    }

    [Fact]
    public void Agent_Should_Not_Allow_Task_Or_Write()
    {
        var agent = Create();

        agent.DeniedTools.Should().Contain(["task", "write", "edit", "delete"]);
    }

    [Fact]
    public void Agent_Should_Allow_Read_Tools()
    {
        var agent = Create();

        // 精确锁定集合而非「包含」：多出任何一个（尤其是 write / edit）都必须被测出来。
        // 复盘 agent 的产出是结构化建议，写盘由宿主执行器完成——它不该有任何写入通道。
        agent.AllowedTools.Should().BeEquivalentTo(["read", "grep", "glob", "skill"],
            "复盘 agent 纯只读，技能写入由宿主侧执行器完成");
    }

    [Fact]
    public void Agent_Should_Deny_Shell()
    {
        var agent = Create();

        agent.PermissionRules.Should().Contain(r =>
            r.Kind == PermissionKind.Shell &&
            r.Effect == PermissionEffect.Deny);
    }

    [Fact]
    public void Agent_Should_Not_Deny_All_Files()
    {
        var agent = Create();

        // read 工具映射到 PermissionKind.File：一旦存在 Deny(File, "*")，read 就永久不可用。
        // 写文件的约束由 DeniedTools 表达，不靠全局 Deny File。
        agent.PermissionRules.Should().NotContain(r =>
            r.Kind == PermissionKind.File &&
            r.Pattern == "*" &&
            r.Effect == PermissionEffect.Deny,
            "全局拒绝 File 会连带拒绝 read 工具");
    }
}
