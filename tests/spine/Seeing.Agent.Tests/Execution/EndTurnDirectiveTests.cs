using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// Task 8：工具经 <see cref="ToolTurnDirective.EndTurn"/> 要求结束本轮时，
/// AgentExecutor 必须在全部工具结束后发出成功的 LoopComplete，
/// Reason 透传工具结果的 <c>TurnDirectiveReason</c>（缺省回退 <c>turn-directive</c>），
/// 且不再进行下一轮 LLM 调用。
/// </summary>
public class EndTurnDirectiveTests
{
    [Fact]
    public async Task ExecuteAsync_ToolReturnsEndTurn_ShouldCompleteLoopAndSkipNextLlmCall()
    {
        var llmCalls = 0;
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
            {
                llmCalls++;
                return StreamToolCall("end_turn_tool");
            });

        var executor = CreateExecutor(llm.Object, manager => manager.RegisterTool(new EndTurnTool()));

        var agent = new AgentDefinition
        {
            Name = "end-turn-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = 5
        };
        var context = new AgentContext
        {
            SessionId = "s1",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root"
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "go" } };

        var events = new List<IMessageEvent>();
        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, TestContext.Current.CancellationToken))
            events.Add(evt);

        // 恰一次终态 LoopComplete，且为成功 + 透传工具原因
        events.OfType<LoopCompleteEvent>().Should().HaveCount(1);
        var complete = events.OfType<LoopCompleteEvent>().Single();
        complete.Success.Should().BeTrue();
        complete.Reason.Should().Be("end-turn-requested");

        // 工具终态事件透传指令
        var toolComplete = events.OfType<ToolCallEvent>()
            .Single(e => e.Status == ToolCallStatus.Success);
        toolComplete.TurnDirective.Should().Be(ToolTurnDirective.EndTurn);
        toolComplete.TurnDirectiveReason.Should().Be("end-turn-requested");

        // 不得进入第二轮 LLM
        llmCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ToolReturnsEndTurnWithoutReason_ShouldFallbackToTurnDirective()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(StreamToolCall("end_turn_tool"));

        var executor = CreateExecutor(llm.Object, manager => manager.RegisterTool(new EndTurnTool(reason: null)));

        var agent = new AgentDefinition
        {
            Name = "end-turn-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = 5
        };
        var context = new AgentContext
        {
            SessionId = "s1",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root"
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "go" } };

        var events = new List<IMessageEvent>();
        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, TestContext.Current.CancellationToken))
            events.Add(evt);

        events.OfType<LoopCompleteEvent>().Single().Reason.Should().Be("turn-directive");
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamToolCall(string name)
    {
        yield return new StreamUpdate
        {
            IsComplete = true,
            FinishReason = "tool_calls",
            ToolCallDeltas = new List<ToolCall>
            {
                new()
                {
                    Id = "c1",
                    Type = "function",
                    Function = new FunctionCall { Name = name, Arguments = "{}" }
                }
            }
        };
    }

    private static AgentExecutor CreateExecutor(
        ILlmService llm,
        Action<ToolManager>? configureTools = null)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        configureTools?.Invoke(toolManager);

        var agentRegistry = new Mock<IAgentRegistry>();

        var permission = new Mock<IPermissionService>();
        permission.Setup(p => p.EvaluateToolAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<PermissionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionResult.Allow(default, "test-allow"));

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        return new AgentExecutor(
            llm,
            toolManager,
            permission.Object,
            hookManager,
            agentRegistry.Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()),
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance);
    }

    private sealed class EndTurnTool : ITool
    {
        private readonly string? _reason;

        public EndTurnTool(string? reason = "end-turn-requested") => _reason = reason;

        public string Id => "end_turn_tool";
        public string Description => "end the current turn";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(new ToolResult
            {
                Success = true,
                Output = "done",
                TurnDirective = ToolTurnDirective.EndTurn,
                TurnDirectiveReason = _reason
            });
    }
}
