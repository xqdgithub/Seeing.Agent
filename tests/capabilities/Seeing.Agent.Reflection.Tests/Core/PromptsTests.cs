using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>复盘提示词的结构与关键约束。</summary>
public sealed class PromptsTests
{
    [Fact]
    public void Prompts_Should_Contain_AntiPattern_List()
    {
        var system = Prompts.ReflectionSystem;

        system.Should().Contain("环境");
        system.Should().Contain("负面断言");
        system.Should().Contain("能从代码");
        system.Should().Contain("一次");
        system.Should().Contain("skip");
    }

    [Fact]
    public void Prompts_Should_Not_ReList_Skill_Catalog()
    {
        // 技能清单由上下文源注入，提示词里再列一遍会重复占用预算。
        Prompts.ReflectionSystem.Should().NotContain("## Skills");
    }

    [Fact]
    public void BuildUserPrompt_Should_Include_Transcript_And_SessionId()
    {
        var prompt = Prompts.BuildUserPrompt("执行记录正文内容", "session-42");

        prompt.Should().Contain("session-42");
        prompt.Should().Contain("执行记录正文内容");
    }

    [Fact]
    public void BuildUserPrompt_Should_Handle_Empty_Transcript()
    {
        var prompt = Prompts.BuildUserPrompt(string.Empty, "session-7");

        prompt.Should().Contain("session-7");
        prompt.Should().NotBeNull();
    }
}
