using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.ToolDiscovery;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tools.ToolDiscovery.Tests;

/// <summary>
/// 输出形态实测：把三种 detail 的真实文本打出来并量 token，用于判断是否值得改结构化。
/// </summary>
public class ToolSearchOutputShapeTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("summary")]
    [InlineData("full")]
    public async Task 输出形态与token成本(string detail)
    {
        var (tool, scope) = BuildTool();
        var args = JsonSerializer.SerializeToElement(new { query = "cron", detail });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "s1",
            Agent = new Seeing.Agent.Abstractions.Agents.AgentDefinition { Name = "build" },
            ToolScope = scope
        });

        result.Success.Should().BeTrue();

        // 定义必须是可读中文，不能是 \uXXXX 转义（每字膨胀 6 倍）。
        if (detail == "full")
        {
            result.Output.Should().Contain("创建定时任务", "非 ASCII 不得被转义");
            result.Output.Should().NotContain("\\u");
        }

        result.Output.Should().NotBeNullOrWhiteSpace();
    }

    private static (ToolSearchTool Tool, IExecutionToolScope Scope) BuildTool()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = "cron_create",
                description = "创建定时任务",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        taskType = new { type = "string", description = "任务类型：text|agent" },
                        prompt = new { type = "string", description = "提醒正文或任务指令" },
                        schedule = new
                        {
                            type = "object",
                            properties = new
                            {
                                type = new { type = "string" },
                                cron = new { type = "string" }
                            },
                            required = new[] { "type" }
                        }
                    },
                    required = new[] { "taskType", "prompt", "schedule" }
                }
            }
        });

        var exposed = new ExposedTool(
            "cron_create",
            Namespace: string.Empty,
            Signature: "cron_create(taskType, prompt, schedule): 创建定时任务",
            Schema: JsonSerializer.Deserialize<FunctionToolSchema>(schema)!,
            EstimatedTokens: 120,
            Mode: ToolExposureMode.Deferred);

        return ToolSearchHarness.Create([exposed]);
    }
}
