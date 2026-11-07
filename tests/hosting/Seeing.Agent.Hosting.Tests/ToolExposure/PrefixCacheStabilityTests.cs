using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.TokenEstimation;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// 前缀缓存稳定性实测：同一会话连续两轮（无 Agent/场景/工具集变化）时，
/// 请求的 <c>tools[]</c> 与系统提示词必须逐字节相同，否则厂商前缀缓存全量失效。
/// </summary>
public class PrefixCacheStabilityTests
{
    [Fact]
    public void 连续两轮结算_tools数组应逐字节相同()
    {
        var tools = Enumerable.Range(0, 40)
            .Select(i => Schema($"tool_{i:00}", "desc"))
            .Append(Schema(ToolIdConventions.SearchToolId, "检索"))
            .ToList();
        var config = new EffectiveExposureConfig(
            ToolExposurePolicyMode.Tiered,
            ["read"],
            CatalogBudgetChars: 8000,
            CatalogLineMaxChars: 200,
            EscalateToolCount: 9999,
            EscalateSchemaTokens: 999999,
            PromoteOnSearch: false);
        var counter = new CharBasedTokenCounter();

        // 第二轮模拟「上一轮发生了 tool_search」：发现集增加，但 promote 关闭。
        var round1 = ToolExposureEngine.Compute(
            new ToolExposureInput(tools, null, tools.Count, [], false, config), counter);
        var round2 = ToolExposureEngine.Compute(
            new ToolExposureInput(tools, null, tools.Count, ["tool_05", "tool_17"], false, config), counter);
        Serialize(round1.Tools).Should().Be(Serialize(round2.Tools));
        round1.CatalogText.Should().Be(round2.CatalogText);
        round1.SchemaTokens.Should().Be(round2.SchemaTokens);
    }

    [Fact]
    public void 搜索后promote关闭_内联集不得增加()
    {
        var tools = new[]
        {
            Schema("read", "读"), Schema("tool_01", "一"),
            Schema(ToolIdConventions.SearchToolId, "检索")
        };
        var config = new EffectiveExposureConfig(
            ToolExposurePolicyMode.Tiered, [], 8000, 200, 9999, 999999, PromoteOnSearch: false);
        var counter = new CharBasedTokenCounter();

        var round1 = ToolExposureEngine.Compute(
            new ToolExposureInput(tools, null, tools.Length, [], false, config), counter);
        var round2 = ToolExposureEngine.Compute(
            new ToolExposureInput(tools, null, tools.Length, ["tool_01"], false, config), counter);

        round1.Tools.Count(t => t.Mode == ToolExposureMode.Inline)
            .Should().Be(round2.Tools.Count(t => t.Mode == ToolExposureMode.Inline),
                "promote 关闭时 tools[] 不得因搜索而增长");
    }

    [Fact]
    public void 目录增量叙述_应追加为消息而非改写系统提示词()
    {
        // 目录变化必须产生一条 context-update 消息；系统提示词内容由 PromptBuilder 决定，
        // 与目录无关（目录已不在任何 IPromptSectionContributor 中）。
        var contributors = new IPromptSectionContributor[]
        {
            new EnvironmentPromptSectionContributor()
        };
        var builder = new PromptBuilder(contributors);
        var agent = new AgentDefinition { Name = "t", SystemPrompt = "BASE\n\n## Environment\n" };
        var context = new PromptContext
        {
            Agent = agent,
            WorkingDirectory = "C:/w",
            WorkspaceRoot = "C:/w",
            Platform = "Win32NT",
            Timestamp = new DateTime(2026, 11, 6, 10, 0, 0),
            Tools = []
        };

        var first = builder.BuildAsync(context).GetAwaiter().GetResult();

        // 模拟第二轮：目录内容既然不在提示词里，改动它对提示词应无影响。
        context.Sections["tool-catalog"] = "任意目录内容";
        var second = builder.BuildAsync(context).GetAwaiter().GetResult();

        second.Should().Be(first, "目录不得再进系统提示词");
        first.Should().NotContain("tool-catalog");
    }

    private static string Serialize(IReadOnlyList<ExposedTool> tools) =>
        JsonSerializer.Serialize(tools.Select(t => new { t.Id, t.Mode, t.Signature }));

    private static FunctionToolSchema Schema(string id, string desc) => new()
    {
        Function = new FunctionSchema
        {
            Name = id,
            Description = desc,
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { path = new { type = "string" } },
                required = new[] { "path" }
            })
        }
    };
}
