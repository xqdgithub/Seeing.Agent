using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Prompts;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// 系统提示词的剩余缓存风险面：除工具目录外，还有哪些每轮可能变化的输入。
/// 这些用例把「稳定」与「必然变化」都钉住，避免下次改动误以为是 bug 或误以为安全。
/// </summary>
public class SystemPromptStabilityTests
{
    private static PromptContext Context(DateTime timestamp) => new()
    {
        Agent = new AgentDefinition { Name = "t", SystemPrompt = "BASE\n" },
        WorkingDirectory = "C:/w",
        WorkspaceRoot = "C:/w",
        Platform = "Win32NT",
        Timestamp = timestamp,
        Tools = []
    };

    [Fact]
    public async Task 环境分节_日期相同_应逐字节相同()
    {
        var builder = new PromptBuilder([new EnvironmentPromptSectionContributor()]);
        var t = new DateTime(2026, 11, 6, 9, 0, 0);

        var a = await builder.BuildAsync(Context(t));
        var b = await builder.BuildAsync(Context(t.AddHours(8)));

        b.Should().Be(a, "同一天内两轮的环境分节必须一致（只到日粒度）");
    }

    [Fact]
    public async Task 环境分节_跨日应变化_这是已知的每日一次缓存断点()
    {
        var builder = new PromptBuilder([new EnvironmentPromptSectionContributor()]);

        var a = await builder.BuildAsync(Context(new DateTime(2026, 11, 6, 23, 59, 0)));
        var b = await builder.BuildAsync(Context(new DateTime(2026, 11, 7, 0, 0, 0)));

        b.Should().NotBe(a, "跨日必然变化：环境分节含 Today's date（已知边界）");
        b.Should().Contain("2026-11-07");
    }

    [Fact]
    public async Task 环境分节_工作目录变化应导致提示词变化()
    {
        // 工作目录在会话内基本不变，但切换工作区/子会话会变——属可接受的显式变化。
        var builder = new PromptBuilder([new EnvironmentPromptSectionContributor()]);
        var context = Context(new DateTime(2026, 11, 6, 9, 0, 0));

        var a = await builder.BuildAsync(context);
        context.WorkingDirectory = "D:/other";
        var b = await builder.BuildAsync(context);

        b.Should().NotBe(a);
        b.Should().Contain("D:/other");
    }

    [Fact]
    public async Task 无目录源时_提示词不含工具目录()
    {
        // 目录必须是上下文源（Message 落点）；任何 IPromptSectionContributor 都不应再渲染它。
        var builder = new PromptBuilder([new EnvironmentPromptSectionContributor()]);
        var context = Context(new DateTime(2026, 11, 6, 9, 0, 0));
        context.Sections["tool-catalog"] = "### 通用 (5 tools, 5 shown)";

        var prompt = await builder.BuildAsync(context);

        prompt.Should().NotContain("### 通用 (5 tools", "目录在系统提示词里会每轮击穿缓存");
    }
}
