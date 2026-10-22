using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
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

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// 执行层可见集校验：本会话本轮未下发 schema 的工具不得被调用。
/// </summary>
public class AgentExecutorToolVisibilityTests
{
    [Fact]
    public async Task ExecuteAsync_InlineTool_ShouldNotBeRejectedByVisibilityGate()
    {
        var executor = CreateExecutor("visible_tool");
        var settlement = BuildSettlement(
            ("visible_tool", ToolExposureMode.Inline));

        var events = await RunAsync(executor, settlement);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().NotBe(ToolCallStatus.Rejected);
    }

    [Fact]
    public async Task ExecuteAsync_DeferredTool_ShouldRejectAndPointToSearch()
    {
        var executor = CreateExecutor("hidden_tool");
        var settlement = BuildSettlement(
            ("visible_tool", ToolExposureMode.Inline),
            ("hidden_tool", ToolExposureMode.Deferred));

        var events = await RunAsync(executor, settlement);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().Be(ToolCallStatus.Rejected);
        complete.Error.Should().Contain(ToolIdConventions.SearchToolId);
    }

    [Fact]
    public async Task ExecuteAsync_ToolAbsentFromSettlement_ShouldRejectWithoutSearchHint()
    {
        var executor = CreateExecutor("never_exposed_tool");
        var settlement = BuildSettlement(
            ("visible_tool", ToolExposureMode.Inline));

        var events = await RunAsync(executor, settlement);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().Be(ToolCallStatus.Rejected);
        complete.Error.Should().NotContain(ToolIdConventions.SearchToolId);
        complete.Error.Should().Contain("不可用");
    }

    [Fact]
    public async Task ExecuteAsync_NullSettlement_ShouldNotGate()
    {
        var executor = CreateExecutor("any_tool");

        var events = await RunAsync(executor, settlement: null);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().NotBe(ToolCallStatus.Rejected);
    }

    private static SessionToolSchemaResolution BuildSettlement(
        params (string Id, ToolExposureMode Mode)[] tools)
    {
        var exposed = tools
            .Select(t => new ExposedTool(
                t.Id,
                Namespace: string.Empty,
                Signature: t.Id,
                Schema: new FunctionToolSchema
                {
                    Function = new FunctionSchema { Name = t.Id, Description = t.Id }
                },
                EstimatedTokens: 10,
                Mode: t.Mode))
            .ToList();

        return new SessionToolSchemaResolution(
            exposed,
            Array.Empty<string>(),
            ToolExposureLayout.Tiered,
            CatalogText: null,
            SchemaTokens: 10,
            CatalogTokens: 0);
    }

    private static async Task<List<IMessageEvent>> RunAsync(
        AgentExecutor executor,
        SessionToolSchemaResolution? settlement)
    {
        var agent = new AgentDefinition
        {
            Name = "visibility-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = 1
        };
        var context = new AgentContext
        {
            SessionId = "s1",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root",
            ToolSettlement = settlement
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        var events = new List<IMessageEvent>();
        await foreach (var evt in executor.ExecuteAsync(
            agent, messages, context, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    private static AgentExecutor CreateExecutor(string callTool = "visible_tool")
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) => StreamToolCallCompletion(callTool));

        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);

        var permission = new Mock<IPermissionService>();
        permission.Setup(p => p.EvaluateToolAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<PermissionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionResult.Allow(default, "test-allow"));

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        return new AgentExecutor(
            llm.Object,
            toolManager,
            permission.Object,
            hookManager,
            new Mock<IAgentRegistry>().Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()),
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance);
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamToolCallCompletion(string toolName)
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
                    Function = new FunctionCall { Name = toolName, Arguments = "{}" }
                }
            }
        };
    }
}
