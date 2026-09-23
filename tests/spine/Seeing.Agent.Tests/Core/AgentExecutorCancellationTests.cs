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
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Core.Tools;
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

    [Fact]
    public async Task ExecuteAsync_CapabilityGateAsk_ShouldPassAgentNameToAuthorizer()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) => StreamToolCallCompletion());

        PermissionRequest? captured = null;
        var authorizer = new Mock<IPermissionAuthorizer>();
        authorizer.SetupGet(a => a.SessionId).Returns("s1");
        authorizer
            .Setup(a => a.AuthorizeAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PermissionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new PermissionResolution
            {
                RequestId = "req",
                SessionId = "s1",
                Decision = PermissionEffect.Deny,
                Reason = "denied"
            });

        var executor = CreateExecutor(llm.Object, permissionResult: PermissionResult.Ask(default, "ask"));

        var agent = new AgentDefinition
        {
            Name = "capability-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = 1
        };
        var context = new AgentContext
        {
            SessionId = "s1",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root",
            PermissionAuthorizer = authorizer.Object
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        await foreach (var _ in executor.ExecuteAsync(agent, messages, context, TestContext.Current.CancellationToken)) { }

        captured.Should().NotBeNull();
        captured!.AgentName.Should().Be("capability-agent");
        captured.PermissionKind.Should().Be("tool.execute");
        captured.Resource.Should().Be("test_tool");
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamPlainCompletion()
    {
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop" };
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamToolCallCompletion()
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
                    Function = new FunctionCall { Name = "test_tool", Arguments = "{}" }
                }
            }
        };
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
        Action<ToolManager>? configureTools = null,
        PermissionResult? permissionResult = null)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        configureTools?.Invoke(toolManager);

        var agentRegistry = new Mock<IAgentRegistry>();

        var permission = new Mock<IPermissionService>();
        permission.Setup(p => p.EvaluateToolAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<PermissionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? _, PermissionContext _, CancellationToken _) =>
                permissionResult ?? PermissionResult.Allow(default, "test-allow"));

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
}
