using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// ExecutionId 贯通回归：执行级 ID 必须从 <see cref="ToolInvocation"/> 投影到
/// <see cref="ToolContext"/>，并覆盖主链（ToolCallInvoker → ToolManager）与直连
/// ToolManager 两条路径。后台活动依赖它归属到「本轮执行」。
/// </summary>
public class ToolContextExecutionIdTests
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

    private static ToolCall CreateToolCall() => new()
    {
        Id = "c1",
        Function = new FunctionCall { Name = "capture", Arguments = "{}" }
    };

    [Fact]
    public async Task ToolManager_ShouldProjectExecutionIdFromInvocation()
    {
        var manager = new ToolManager(
            NullLogger<ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));
        var tool = new CapturingTool();
        await manager.RegisterToolAsync(tool);

        await manager.ExecuteAsync(
            CreateToolCall(),
            new ToolInvocation(
                "s1", string.Empty, null, null, null, null, null, "exec-42"),
            TestContext.Current.CancellationToken);

        tool.Captured.Should().NotBeNull();
        tool.Captured!.ExecutionId.Should().Be("exec-42");
    }

    [Fact]
    public async Task ToolManager_ShouldLeaveExecutionIdNullWhenInvocationOmitsIt()
    {
        var manager = new ToolManager(
            NullLogger<ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));
        var tool = new CapturingTool();
        await manager.RegisterToolAsync(tool);

        await manager.ExecuteAsync(
            CreateToolCall(),
            new ToolInvocation("s1", string.Empty, null, null, null, null, null),
            TestContext.Current.CancellationToken);

        tool.Captured.Should().NotBeNull();
        tool.Captured!.ExecutionId.Should().BeNull();
    }

    [Fact]
    public async Task ToolCallInvoker_ShouldForwardAgentContextExecutionIdToToolContext()
    {
        var manager = new ToolManager(
            NullLogger<ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));
        var tool = new CapturingTool();
        await manager.RegisterToolAsync(tool);

        // 能力门放行（不进入审批分支），让调用真正走到工具执行。
        var permissions = new Mock<IPermissionService>();
        permissions
            .Setup(p => p.EvaluateToolAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<PermissionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionResult.Allow(default, "ok"));

        var invoker = new ToolCallInvoker(
            manager,
            permissions.Object,
            NullLogger.Instance);

        var agent = new AgentDefinition { Name = "build" };
        var context = new AgentContext
        {
            SessionId = "s1",
            ExecutionId = "exec-7",
            WorkingDirectory = string.Empty,
            CancellationToken = TestContext.Current.CancellationToken
        };

        var evt = await invoker.InvokeAsync(
            CreateToolCall(),
            agent,
            context,
            permissionAuthorizer: null,
            loopId: "loop-1",
            toolScope: null,
            emitAsync: null,
            cancellationToken: TestContext.Current.CancellationToken);

        evt.Status.Should().Be(ToolCallStatus.Success);
        tool.Captured.Should().NotBeNull();
        tool.Captured!.ExecutionId.Should().Be("exec-7");
    }
}
