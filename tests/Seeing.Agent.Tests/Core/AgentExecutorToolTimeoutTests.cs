using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// AgentExecutor 最外层全局兜底超时测试
/// <para>验证：全局兜底超时（ToolExecutionTimeout）触发后，统一报告"执行超时"，不误报为取消。</para>
/// </summary>
public class AgentExecutorToolTimeoutTests
{
    /// <summary>
    /// 挂起工具：直到取消令牌触发，模拟无内部超时的第三方工具
    /// </summary>
    private sealed class HangingTool : ITool
    {
        public string Id => "hang";
        public string Description => "挂起工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), context.CancellationToken);
            return new ToolResult { Success = true, Output = "unreachable" };
        }
    }

    [Fact]
    public async Task ExecuteAsync_GlobalToolTimeout_ShouldReportTimeoutNotCancelled()
    {
        var llm = new Mock<ILlmService>();
        var callCount = 0;
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++callCount == 1 ? StreamToolCall() : StreamCompletion());

        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue)
            .Returns(new SeeingAgentOptions { ToolExecutionTimeout = TimeSpan.FromMilliseconds(200) });

        var executor = CreateExecutor(llm.Object, options.Object, tm => tm.RegisterTool(new HangingTool()));
        using var cts = new CancellationTokenSource();

        var events = new List<IMessageEvent>();
        await foreach (var evt in EnumerateWithCancel(executor, cts))
            events.Add(evt);

        var failed = events.OfType<ToolCallEvent>()
            .FirstOrDefault(e => e.Status == ToolCallStatus.Failed);
        failed.Should().NotBeNull("应报告工具执行失败");
        failed!.Error.Should().Contain("执行超过全局超时");
        events.Should().NotContain(e => e is LoopCancelledEvent);
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamToolCall()
    {
        yield return new StreamUpdate
        {
            IsComplete = true,
            FinishReason = "tool_calls",
            ToolCallDeltas = new List<ToolCall>
            {
                new()
                {
                    Id = "call_1",
                    Function = new FunctionCall { Name = "hang", Arguments = "{}" }
                }
            }
        };
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamCompletion()
    {
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop" };
    }

    private static async IAsyncEnumerable<IMessageEvent> EnumerateWithCancel(
        AgentExecutor executor, CancellationTokenSource cts)
    {
        var agent = new AgentDefinition
        {
            Name = "test",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            SystemPrompt = null
        };
        var context = new AgentContext
        {
            SessionId = "s1",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root",
            CancellationToken = cts.Token
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, cts.Token))
            yield return evt;
    }

    private static AgentExecutor CreateExecutor(
        ILlmService llm,
        IOptionsMonitor<SeeingAgentOptions> options,
        Action<ToolManager>? configureTools = null)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        configureTools?.Invoke(toolManager);

        var agentRegistry = new Mock<IAgentRegistry>();

        var permission = new Mock<IPermissionService>();
        permission.Setup(p => p.EvaluateToolAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<PermissionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? _, PermissionContext _, CancellationToken _) =>
                PermissionResult.Allow(default, "test-allow"));

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        return new AgentExecutor(
            llm,
            toolManager,
            permission.Object,
            hookManager,
            agentRegistry.Object,
            new PromptBuilder(agentRegistry.Object, null!),
            options,
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance);
    }
}
