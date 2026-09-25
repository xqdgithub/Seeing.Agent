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

namespace Seeing.Agent.Tests.Detection;

/// <summary>
/// AgentExecutor 的 LoopDetector 接线测试：验证「同一工具 + 同一参数」连续调用的
/// 3 次警告 / 5 次终止语义真正在 Llm 循环生产路径生效。
/// </summary>
public class AgentExecutorLoopDetectionTests
{
    private const string LoopSource = "loop";

    [Fact]
    public async Task ExecuteAsync_SameToolSameArgsFiveTimes_ShouldTerminateWithLoopReason()
    {
        var llmCalls = 0;
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
            {
                llmCalls++;
                return StreamToolCall("loop_tool", "{}");
            });

        var executor = await CreateExecutorAsync(llm.Object, new LoopTool());
        var events = await Run(executor, maxSteps: 20);

        var completes = events.OfType<LoopCompleteEvent>().ToList();
        completes.Should().HaveCount(1);
        completes[0].Success.Should().BeFalse();
        completes[0].Reason.Should().Be("loop-detected");
        completes[0].Error.Should().Contain("循环");
        completes[0].Error.Should().Contain("loop_tool");

        // 连续第 3、4 次各一条警告（Source=loop），第 5 次直接终止。
        events.OfType<ErrorEvent>().Where(e => e.Source == LoopSource).Should().HaveCount(2);

        // 第 5 次（触发终止那一步）之后不得再有第 6 次 LLM 调用。
        llmCalls.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_SameToolSameArgsThreeOrFourTimes_ShouldWarnButNotTerminate()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(StreamToolCall("loop_tool", "{}"));

        var executor = await CreateExecutorAsync(llm.Object, new LoopTool());
        var events = await Run(executor, maxSteps: 4);

        // 3、4 次各一条警告，但未触发终止（走满最大步数）。
        events.OfType<ErrorEvent>().Where(e => e.Source == LoopSource).Should().HaveCount(2);

        var complete = events.OfType<LoopCompleteEvent>().Single();
        complete.Success.Should().BeFalse();
        complete.Reason.Should().NotBe("loop-detected");
        complete.Error.Should().Contain("最大步数");
    }

    [Fact]
    public async Task ExecuteAsync_SameToolDifferentArgs_ShouldNotAccumulate()
    {
        var counter = 0;
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
            {
                counter++;
                return StreamToolCall("loop_tool", $"{{\"n\":{counter}}}");
            });

        var executor = await CreateExecutorAsync(llm.Object, new LoopTool());
        var events = await Run(executor, maxSteps: 6);

        // 参数每次都不同，不应累计为循环。
        events.OfType<ErrorEvent>().Where(e => e.Source == LoopSource).Should().BeEmpty();

        var complete = events.OfType<LoopCompleteEvent>().Single();
        complete.Reason.Should().NotBe("loop-detected");
        complete.Error.Should().Contain("最大步数");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ExecuteAsync_SameToolWithoutArguments_FiveTimes_ShouldTerminateWithoutThrowing(string? arguments)
    {
        // 无参数工具（arguments 为 null / 空串）不应在 LoopDetector 处抛异常，
        // 且同样应累计为循环并于第 5 次终止。
        var llmCalls = 0;
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
            {
                llmCalls++;
                return StreamToolCall("loop_tool", arguments);
            });

        var executor = await CreateExecutorAsync(llm.Object, new LoopTool());
        var events = await Run(executor, maxSteps: 20);

        var complete = events.OfType<LoopCompleteEvent>().Single();
        complete.Success.Should().BeFalse();
        complete.Reason.Should().Be("loop-detected");
        complete.Error.Should().Contain("循环");

        // 第 3、4 次各一条警告，第 5 次终止。
        events.OfType<ErrorEvent>().Where(e => e.Source == LoopSource).Should().HaveCount(2);
        llmCalls.Should().Be(5);
    }

    private static async Task<List<IMessageEvent>> Run(AgentExecutor executor, int maxSteps)
    {
        var agent = new AgentDefinition
        {
            Name = "loop-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = maxSteps
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
        return events;
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

    private static async Task<AgentExecutor> CreateExecutorAsync(ILlmService llm, ITool? tool = null)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        if (tool != null)
            await toolManager.RegisterToolAsync(tool);

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

    private sealed class LoopTool : ITool
    {
        public string Id => "loop_tool";
        public string Description => "loop detection probe";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(new ToolResult { Success = true, Output = "ok" });
    }
}
