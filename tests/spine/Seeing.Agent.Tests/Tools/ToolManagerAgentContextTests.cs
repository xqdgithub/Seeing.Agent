using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// I3 回归：ToolManager 执行工具时须把当前 Agent 定义写入 ToolContext.Agent，
/// 使资源门（如 MCP 的 mcp.execute）能读取 AgentName 应用 Agent 策略。
/// </summary>
public class ToolManagerAgentContextTests
{
    private sealed class CapturingTool : ITool
    {
        public string Id => "capture";
        public string Description => "捕获上下文";
        public IReadOnlyList<string> Tags => [];
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolContext? Captured { get; private set; }

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            Captured = context;
            return Task.FromResult(new ToolResult { Success = true, Output = "ok" });
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithAgent_ShouldPopulateContextAgent()
    {
        var hooks = new HookManager(NullLogger<HookManager>.Instance);
        var manager = new ToolManager(NullLogger<ToolManager>.Instance, hooks);
        var tool = new CapturingTool();
        await manager.RegisterToolAsync(tool);

        var agent = new AgentDefinition { Name = "plan" };
        var toolCall = new ToolCall
        {
            Id = "c1",
            Function = new FunctionCall { Name = "capture", Arguments = "{}" }
        };

        await manager.ExecuteAsync(
            toolCall,
            "s1",
            TestContext.Current.CancellationToken,
            emitAsync: null,
            permissionAuthorizer: null,
            agent: agent);

        tool.Captured.Should().NotBeNull();
        tool.Captured!.Agent.Should().BeSameAs(agent);
        tool.Captured.Agent!.Name.Should().Be("plan");
    }
}
