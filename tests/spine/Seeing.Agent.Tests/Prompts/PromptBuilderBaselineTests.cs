using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Prompts;
using Xunit;

namespace Seeing.Agent.Tests.Prompts;

/// <summary>
/// 基线追加时机锁定：必须在分节注入、变量替换与 Trim 之后追加，
/// 使正文中的 <c>{{...}}</c> 字面量原样进入提示词（Review Focus #2）。
/// </summary>
public class PromptBuilderBaselineTests
{
    [Fact]
    public async Task Baseline_ShouldNotBeVariableSubstituted()
    {
        var ctx = new PromptContext
        {
            Agent = new AgentDefinition { SystemPrompt = "T {{timestamp}}" },
            Timestamp = DateTime.Now,
            ContextBaseline = "## Skills\nkeep {{timestamp}} literal"
        };

        var result = await new PromptBuilder([]).BuildAsync(ctx, default);

        result.Should().Contain("## Skills");
        result.Should().Contain("keep {{timestamp}} literal");
    }

    [Fact]
    public async Task Baseline_ShouldBeAppendedLast()
    {
        var ctx = new PromptContext
        {
            Agent = new AgentDefinition { SystemPrompt = "BODY" },
            ContextBaseline = "TAIL"
        };

        (await new PromptBuilder([]).BuildAsync(ctx, default)).Should().EndWith("TAIL");
    }

    [Fact]
    public async Task EmptyAgentPrompt_ShouldUseBaselineAsSystemPrompt()
    {
        var ctx = new PromptContext
        {
            Agent = new AgentDefinition { SystemPrompt = "" },
            ContextBaseline = "ONLY-BASELINE"
        };

        (await new PromptBuilder([]).BuildAsync(ctx, default)).Should().Be("ONLY-BASELINE");
    }
}
