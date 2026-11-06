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
    public async Task 目录源未注册时_提示词不含目录正文()
    {
        // Sections 槽位已不再被任何分节贡献者消费（ToolCatalogSectionContributor 已删除）。
        // 目录只能经上下文源基线进入系统提示词——这条保证"只有一条路径"。
        var builder = new PromptBuilder([new EnvironmentPromptSectionContributor()]);
        var context = Context(new DateTime(2026, 11, 6, 9, 0, 0));
        context.Sections["tool-catalog"] = "### 通用 (5 tools, 5 shown)";

        var prompt = await builder.BuildAsync(context);

        prompt.Should().NotContain("### 通用 (5 tools", "目录只能经上下文源基线进入，不得走分节槽位");
    }

    [Fact]
    public async Task 目录基线已存_两轮系统提示词应逐字节相同()
    {
        // System 落点的关键性质：BuildSystemBaseline 只读存库基线，不重算，
        // 所以基线建立后系统提示词稳定；后续目录变化走增量叙述消息。
        var builder = new PromptBuilder([new EnvironmentPromptSectionContributor()]);
        var context = Context(new DateTime(2026, 11, 6, 9, 0, 0));
        context.ContextBaseline = "## Tool Catalog\n\n### 通用 (5 tools, 5 shown)";

        var first = await builder.BuildAsync(context);
        var second = await builder.BuildAsync(context);

        second.Should().Be(first);
        first.Should().Contain("### 通用 (5 tools, 5 shown)");
    }
}
