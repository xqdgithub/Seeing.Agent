using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.ToolDiscovery;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tools.ToolDiscovery.Tests;

/// <summary>
/// tool_search 输出的信息分层：分页元信息（头/尾）与真实结果（主体）必须能一眼区分。
/// <para>
/// 约定：元信息一律用 <c>[ ]</c> 标记并单独成段，结果条目用 <c>N. </c> 编号；
/// 检索级事实（如发现态）只出现一次，不随每个条目重复。
/// </para>
/// </summary>
public class ToolSearchOutputLayeringTests
{
    [Fact]
    public async Task 头部元信息_应单独成段且有标记()
    {
        var output = await RunAsync(detail: "summary", pages: 3, limit: 2);

        var firstLine = output.Split('\n')[0];
        firstLine.Should().StartWith("[", "元信息段必须以标记开头，与结果条目区分");
        firstLine.Should().Contain("total");
        output.Should().Contain(Environment.NewLine + Environment.NewLine,
            "元信息段与结果之间必须有空行分隔");
    }

    [Fact]
    public async Task 结果条目_应紧跟元信息段之后()
    {
        var output = await RunAsync(detail: "summary", pages: 3, limit: 2);

        var sections = output.Split($"{Environment.NewLine}{Environment.NewLine}",
            StringSplitOptions.RemoveEmptyEntries);
        sections.Should().HaveCountGreaterThan(1);
        sections[0].Should().StartWith("[", "第一段是元信息");
        sections[1].Should().StartWith("1. ", "第二段直接是编号结果，中间无样板句");
    }

    [Fact]
    public async Task 分页提示_应在末尾独立成段_不夹在结果中间()
    {
        var output = await RunAsync(detail: "summary", pages: 3, limit: 2);

        var sections = output.Split($"{Environment.NewLine}{Environment.NewLine}",
            StringSplitOptions.RemoveEmptyEntries);
        var last = sections[^1];

        last.Should().StartWith("[", "分页/后续动作提示放末尾元信息段");
        last.Should().Contain("offset=");
        // 结果段里不得出现翻页提示
        sections[1].Should().NotContain("offset=");
    }

    [Fact]
    public async Task full模式_发现态事实只出现一次()
    {
        var output = await RunAsync(detail: "full", pages: 3, limit: 3);

        var occurrences = output.Split("可直接调用").Length - 1;
        occurrences.Should().Be(1, "发现态是整次检索级事实，不得随每个条目重复");
    }

    [Fact]
    public async Task 无剩余页_不应出现翻页提示()
    {
        var output = await RunAsync(detail: "summary", pages: 1, limit: 10);

        output.Should().NotContain("offset=");
    }

    [Fact]
    public async Task full模式_定义应只含内层_不带function包装()
    {
        // {"type":"function","function":{...}} 这层包装对模型没有信息量：
        // 它不需要知道这是 OpenAI 的 function schema 格式，且与 summary 的形态不连续。
        var output = await RunAsync(detail: "full", pages: 1, limit: 1);

        output.Should().Contain("\"name\":");
        output.Should().Contain("\"parameters\":");
        output.Should().NotContain("\"type\":\"function\"",
            "外层包装是传输格式细节，不该进模型可见文本");
    }

    // ===== 脚手架 =====

    private static async Task<string> RunAsync(string detail, int pages, int limit)
    {
        var corpus = Enumerable.Range(0, pages)
            .Select(i => Entry($"cron_tool_{i}", $"第 {i} 个定时任务工具"))
            .ToArray();

        var (tool, scope) = ToolSearchHarness.Create(corpus);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { query = "cron", detail, limit }),
            new ToolContext
            {
                SessionId = "s1",
                Agent = new Seeing.Agent.Abstractions.Agents.AgentDefinition { Name = "build" },
                ToolScope = scope
            });

        result.Success.Should().BeTrue(result.Error);
        return result.Output;
    }

    private static ExposedTool Entry(string id, string description) => new(
        id,
        Namespace: string.Empty,
        Signature: $"{id}(taskType, prompt): {description}",
        Schema: JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = id,
                description,
                parameters = new
                {
                    type = "object",
                    properties = new { taskType = new { type = "string" } },
                    required = new[] { "taskType" }
                }
            }
        }).Deserialize<FunctionToolSchema>()!,
        EstimatedTokens: 60,
        Mode: ToolExposureMode.Deferred);
}
