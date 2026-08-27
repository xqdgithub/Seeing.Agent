using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class AgentExecutorCancellationTests
{
    [Fact]
    public async Task ExecuteAsync_NoToolCalls_ShouldEmitLoopCompleteSuccessAsLastEvent()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) => StreamPlainCompletion());

        var executor = CreateExecutor(llm.Object);
        using var cts = new CancellationTokenSource();

        var events = new List<IMessageEvent>();
        await foreach (var evt in EnumerateWithCancel(executor, cts))
            events.Add(evt);

        events.Last().Should().BeOfType<LoopCompleteEvent>().Which.Success.Should().BeTrue();
        events.Should().NotContain(e => e is LoopCancelledEvent);
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamPlainCompletion()
    {
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop" };
    }

    /// <summary>枚举事件流，同时让调用方可从外部观察（本轮未取消，仅返回全部事件）。</summary>
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
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance);
    }
}
