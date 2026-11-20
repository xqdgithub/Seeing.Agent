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
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// loop.skip 能力键接线测试：声明该能力的轮询类工具即使以同一参数反复调用，
/// 也不得被 Doom Loop 检测警告或终止；未声明的工具保持既有 3 警告 / 5 终止语义。
/// </summary>
public class LoopSkipCapabilityTests
{
    private const string LoopSource = "loop";
    private const string ToolId = "poll_tool";

    [Fact]
    public async Task Tool_WithLoopSkip_ShouldNotTriggerLoopDetection_AfterFiveCalls()
    {
        var llmCalls = 0;
        var toolCalls = 0;
        var llm = CreateRepeatingLlm(() => llmCalls++);
        var tool = new FakeTool(declareLoopSkip: true, onExecute: () => toolCalls++);

        var executor = await CreateExecutorAsync(llm.Object, tool);
        var events = await Run(executor, maxSteps: 6);

        // 6 次同参调用全部真正执行，且没有任何 loop 警告 / 终止。
        llmCalls.Should().Be(6);
        toolCalls.Should().Be(6);
        events.OfType<ErrorEvent>().Where(e => e.Source == LoopSource).Should().BeEmpty();

        var complete = events.OfType<LoopCompleteEvent>().Single();
        complete.Reason.Should().NotBe("loop-detected");
        complete.Error.Should().Contain("最大步数");
    }

    [Fact]
    public async Task Tool_WithoutLoopSkip_ShouldStillTriggerLoopDetection()
    {
        var llmCalls = 0;
        var toolCalls = 0;
        var llm = CreateRepeatingLlm(() => llmCalls++);
        var tool = new FakeTool(declareLoopSkip: false, onExecute: () => toolCalls++);

        var executor = await CreateExecutorAsync(llm.Object, tool);
        var events = await Run(executor, maxSteps: 20);

        // 第 3、4 次各一条警告，第 5 次终止；第 5 次的工具调用不得执行。
        events.OfType<ErrorEvent>().Where(e => e.Source == LoopSource).Should().HaveCount(2);
        llmCalls.Should().Be(5);
        toolCalls.Should().Be(4);

        var complete = events.OfType<LoopCompleteEvent>().Single();
        complete.Success.Should().BeFalse();
        complete.Reason.Should().Be("loop-detected");
        complete.Error.Should().Contain("循环");
    }

    [Fact]
    public async Task Tool_WithLoopSkip_CaseVariantName_ShouldStillBeExempt()
    {
        // 模型可能写大小写变体；能力读取必须发生在结算归一化之后，
        // 否则查不到注册表里的 canonical 工具，豁免会静默失效。
        var llmCalls = 0;
        var llm = CreateRepeatingLlm(() => llmCalls++, "polltool");
        var tool = new FakeTool(declareLoopSkip: true, onExecute: () => { }, id: "PollTool");

        var executor = await CreateExecutorAsync(llm.Object, tool);
        var events = await Run(executor, maxSteps: 6, settlementTool: tool);

        llmCalls.Should().Be(6);
        events.OfType<ErrorEvent>().Where(e => e.Source == LoopSource).Should().BeEmpty();

        var complete = events.OfType<LoopCompleteEvent>().Single();
        complete.Reason.Should().NotBe("loop-detected");
    }

    private static Mock<ILlmService> CreateRepeatingLlm(Action onCall, string toolName = ToolId)
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
            {
                onCall();
                return StreamToolCall(toolName, "{}");
            });
        return llm;
    }

    private static async Task<List<IMessageEvent>> Run(
        AgentExecutor executor, int maxSteps, ITool? settlementTool = null)
    {
        var agent = new AgentDefinition
        {
            Name = "loop-skip-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = maxSteps
        };
        var context = new AgentContext
        {
            SessionId = "s-loop-skip",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root",
            ToolSettlement = settlementTool is null ? null : BuildSettlement(settlementTool)
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "go" } };

        var events = new List<IMessageEvent>();
        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, TestContext.Current.CancellationToken))
            events.Add(evt);
        return events;
    }

    private static SessionToolSchemaResolution BuildSettlement(ITool tool)
    {
        var schema = new FunctionToolSchema
        {
            Function = new FunctionSchema
            {
                Name = tool.Id,
                RealName = tool.Id,
                Description = tool.Description,
                Parameters = tool.ParametersSchema
            }
        };
        return new SessionToolSchemaResolution(
            new[] { new ExposedTool(tool.Id, "", tool.Id, schema, 0, ToolExposureMode.Inline) },
            Array.Empty<string>(),
            ToolExposureLayout.Flat,
            null,
            0,
            0);
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamToolCall(string name, string? arguments)
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
                    Function = new FunctionCall { Name = name, Arguments = arguments }
                }
            }
        };
        await Task.CompletedTask;
    }

    private static async Task<AgentExecutor> CreateExecutorAsync(ILlmService llm, ITool tool)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        await toolManager.RegisterToolAsync(tool);

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
            new Mock<IAgentRegistry>().Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()),
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance);
    }

    private sealed class FakeTool : ITool
    {
        private readonly Action _onExecute;

        public FakeTool(bool declareLoopSkip, Action onExecute, string id = ToolId)
        {
            _onExecute = onExecute;
            Id = id;
            Capabilities = declareLoopSkip
                ? new Dictionary<string, string> { [ToolCapabilityKeys.LoopSkip] = "true" }
                : new Dictionary<string, string>();
        }

        public string Id { get; }
        public string Description => "polling probe";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public IReadOnlyDictionary<string, string>? Capabilities { get; }

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            _onExecute();
            return Task.FromResult(new ToolResult { Success = true, Output = "ok" });
        }
    }
}
