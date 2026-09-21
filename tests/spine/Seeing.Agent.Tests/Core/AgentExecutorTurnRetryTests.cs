using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class AgentExecutorTurnRetryTests
{
    private sealed class CallCounter { public int Value; }

    private static async IAsyncEnumerable<StreamUpdate> FailThenSucceed(CallCounter counter)
    {
        if (counter.Value++ == 0)
            throw new LlmStreamingException("流式响应读取失败", new IOException("remote closed"));
        yield return new StreamUpdate { ContentDelta = "ok", IsComplete = true, FinishReason = "stop" };
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteAsync_StreamFailsOnce_RetriesAndProducesSingleContent()
    {
        var calls = new CallCounter();
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) => FailThenSucceed(calls));

        var executor = CreateExecutor(llm.Object, new DefaultLlmTurnRetryPolicy());
        var events = new List<IMessageEvent>();
        await foreach (var e in Enumerate(executor)) events.Add(e);

        calls.Value.Should().Be(2);
        events.OfType<StreamStartEvent>().Should().HaveCount(2);
        events.OfType<StreamStartEvent>().Last().Attempt.Should().Be(2);
        events.OfType<LlmRetryScheduledEvent>().Should().ContainSingle()
            .Which.Attempt.Should().Be(2);
        events.OfType<StreamDeltaEvent>().Select(d => d.ContentDelta).Where(c => c != null)
            .Aggregate("", (a, b) => a + b).Should().Be("ok");
        events.OfType<LoopCompleteEvent>().Last().Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NonRetryableFailure_DoesNotRetry()
    {
        var calls = new CallCounter();
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) =>
                FailWith(calls.Value++, new HttpRequestException("400", null, System.Net.HttpStatusCode.BadRequest)));

        var executor = CreateExecutor(llm.Object, new DefaultLlmTurnRetryPolicy());
        var events = new List<IMessageEvent>();
        await foreach (var e in Enumerate(executor)) events.Add(e);

        calls.Value.Should().Be(1);
        events.OfType<LlmRetryScheduledEvent>().Should().BeEmpty();
        events.OfType<LoopCompleteEvent>().Last().Success.Should().BeFalse();
    }

    private static async IAsyncEnumerable<StreamUpdate> FailWith(int _, Exception ex)
    {
        await Task.CompletedTask;
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IMessageEvent> Enumerate(AgentExecutor executor)
    {
        var agent = new AgentDefinition { Name = "t", Runtime = AgentRuntime.Native, Mode = AgentMode.All };
        var context = new AgentContext { SessionId = "s1", WorkingDirectory = "w", WorkspaceRoot = "w" };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };
        await foreach (var e in executor.ExecuteAsync(agent, messages, context, default))
            yield return e;
    }

    private static AgentExecutor CreateExecutor(ILlmService llm, ILlmTurnRetryPolicy? retryPolicy)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        var agentRegistry = new Mock<IAgentRegistry>();
        var permission = new Mock<IPermissionService>();
        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        return new AgentExecutor(
            llm, toolManager, permission.Object, hookManager, agentRegistry.Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()), modelManager.Object,
            NullLogger<AgentExecutor>.Instance,
            sessionManager: null, loopScheduler: null, todoStore: null, turnRetryPolicy: retryPolicy);
    }
}
