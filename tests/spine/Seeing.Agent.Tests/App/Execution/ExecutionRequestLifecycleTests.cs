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
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 校验 <see cref="ChatRequest"/> 的生命周期：每次 attempt 必须拿到全新实例，
/// 避免重试路径复用（并被 LlmService 改写过的）可变请求对象。
/// </summary>
public class ExecutionRequestLifecycleTests
{
    /// <summary>记录每次尝试收到的请求实例；Mock 回调可能在不同线程执行，故加锁。</summary>
    private sealed class RequestCapture
    {
        private readonly List<ChatRequest> _requests = new();
        private int _calls;

        public void Add(ChatRequest request)
        {
            lock (_requests)
            {
                _requests.Add(request);
                _calls++;
            }
        }

        /// <summary>本次调用序号（1 基；0 表示尚未调用）。</summary>
        public int Calls
        {
            get { lock (_requests) return _calls; }
        }

        public IReadOnlyList<ChatRequest> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }
    }

    [Fact]
    public async Task Each_Retry_Attempt_Should_Get_A_Fresh_Request()
    {
        // Arrange：首次抛可重试异常，第二次正常返回
        var captured = new RequestCapture();
        var executor = BuildExecutor(captured, failFirstAttempt: true);

        // Act
        await foreach (var _ in Enumerate(executor)) { }

        // Assert
        var requests = captured.Requests;
        requests.Should().HaveCount(2);
        requests[0].Should().NotBeSameAs(requests[1]);
    }

    [Fact]
    public async Task Normal_Path_Should_Build_Request_Once()
    {
        // Arrange：一次成功，不应产生额外请求构建
        var captured = new RequestCapture();
        var executor = BuildExecutor(captured, failFirstAttempt: false);

        // Act
        await foreach (var _ in Enumerate(executor)) { }

        // Assert
        captured.Requests.Should().HaveCount(1);
    }

    /// <summary>消费默认 Agent 的整个执行事件流。</summary>
    private static async IAsyncEnumerable<IMessageEvent> Enumerate(AgentExecutor executor)
    {
        var agent = new AgentDefinition { Name = "t", Runtime = AgentRuntime.Native, Mode = AgentMode.All };
        var context = new AgentContext { SessionId = "s1", WorkingDirectory = "w", WorkspaceRoot = "w" };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        await foreach (var e in executor.ExecuteAsync(agent, messages, context, default))
            yield return e;
    }

    /// <summary>按 <see cref="AgentExecutorTurnRetryTests"/> 的 11 参数范式构造执行器。</summary>
    private static AgentExecutor BuildExecutor(RequestCapture captured, bool failFirstAttempt)
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest request, string? _, CancellationToken _) =>
            {
                captured.Add(request);
                return failFirstAttempt && captured.Calls == 1
                    ? FailThenSucceed()
                    : Succeed();
            });

        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        var agentRegistry = new Mock<IAgentRegistry>();
        var permission = new Mock<IPermissionService>();
        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        var retryPolicy = new DefaultLlmTurnRetryPolicy(new LlmTurnRetryOptions
        {
            MaxAttempts = 2,
            BaseDelayMs = 1
        });

        return new AgentExecutor(
            llm.Object, toolManager, permission.Object, hookManager, agentRegistry.Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()), modelManager.Object,
            NullLogger<AgentExecutor>.Instance,
            sessionManager: null, todoStore: null, turnRetryPolicy: retryPolicy);
    }

    /// <summary>首轮抛出可重试的流式异常（LlmStreamingException 包装 IOException）。</summary>
    private static async IAsyncEnumerable<StreamUpdate> FailThenSucceed()
    {
        await Task.CompletedTask;
        throw new LlmStreamingException("流式响应读取失败", new IOException("remote closed"));
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>正常完成一轮流式响应。</summary>
    private static async IAsyncEnumerable<StreamUpdate> Succeed()
    {
        yield return new StreamUpdate { ContentDelta = "ok", IsComplete = true, FinishReason = "stop" };
        await Task.CompletedTask;
    }
}
