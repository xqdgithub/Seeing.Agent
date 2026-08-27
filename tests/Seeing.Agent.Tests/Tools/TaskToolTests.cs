using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Execution;
using Seeing.Agent.Llm;
using Seeing.Agent.Models;
using Seeing.Agent.Tools.BuiltIn.SubTask;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// TaskTool：子会话 = 普通会话，创建子会话 + 提交执行引擎（ExecutionJobService）。
/// </summary>
public class TaskToolTests
{
    [Fact]
    public async Task ExecuteAsync_Foreground_ShouldSubmitExecutionAndReturnCompleted()
    {
        using var fixture = new TaskToolFixture(executor: BuildExecutor("final answer"));
        var tool = new TaskTool(
            NullLogger<TaskTool>.Instance,
            fixture.SessionManager.Object,
            fixture.AgentRegistry.Object,
            fixture.LoopScheduler.Object);
        var context = new ToolContext
        {
            SessionId = fixture.ParentId,
            CallId = "call-1",
            Services = fixture.ToolProvider
        };

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth config",
                subagent_type = "explore"
            }),
            context);

        result.Success.Should().BeTrue();
        result.Title.Should().Be("explore auth");
        result.Output.Should().Contain($"task_id: {fixture.Child.Id}");
        result.Output.Should().Contain("state: completed");
        result.Output.Should().Contain("<task_result>");
        result.Output.Should().Contain("final answer");

        // 子会话应有 assistant 消息（由执行引擎投影落盘）
        fixture.Child.GetActiveMessages()
            .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            .Should().NotBeNull();

        // 执行记录应为终态 Completed
        var overview = fixture.ExecService.GetOverview(fixture.Child.Id);
        overview.HasActiveExecution.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Background_ShouldReturnRunningAndNotifyParentWhenCompleted()
    {
        using var fixture = new TaskToolFixture(executor: BuildExecutor("background result", delayMs: 300));
        var tool = new TaskTool(
            NullLogger<TaskTool>.Instance,
            fixture.SessionManager.Object,
            fixture.AgentRegistry.Object,
            fixture.LoopScheduler.Object);
        var context = new ToolContext
        {
            SessionId = fixture.ParentId,
            CallId = "call-1",
            Services = fixture.ToolProvider
        };

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth config",
                subagent_type = "explore",
                background = true
            }),
            context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("state: running");

        // 等待后台完成 → 向父会话注入 synthetic 完成通知
        await WaitUntilAsync(
            () => fixture.SyntheticInvocations.Count > 0,
            timeoutMs: 8000);

        var (sessionId, text, _) = fixture.SyntheticInvocations.Single();
        sessionId.Should().Be(fixture.ParentId);
        text.Should().Contain("Background task completed");
        text.Should().Contain($"task_id: {fixture.Child.Id}");
        text.Should().Contain("state: completed");
        text.Should().Contain("<task_result>");
        text.Should().Contain("background result");

        fixture.LoopScheduler.Verify(
            l => l.TryResumeWhenIdleAsync(fixture.ParentId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAgent_ShouldReturnFailure()
    {
        using var fixture = new TaskToolFixture();
        var tool = new TaskTool(
            NullLogger<TaskTool>.Instance,
            fixture.SessionManager.Object,
            fixture.AgentRegistry.Object,
            fixture.LoopScheduler.Object);
        var context = new ToolContext { SessionId = fixture.ParentId, Services = fixture.ToolProvider };

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "x",
                prompt = "y",
                subagent_type = "does-not-exist"
            }),
            context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未知的 Agent 类型");
    }

    private static IAgentExecutor BuildExecutor(string content = "final answer", int delayMs = 0)
    {
        var mock = new Mock<IAgentExecutor>();
        mock.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken ct)
                => AssistantStream(content, delayMs, ct));
        return mock.Object;
    }

    private static async IAsyncEnumerable<IMessageEvent> AssistantStream(
        string content, int delayMs, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs, ct);
        yield return new StreamCompleteEvent
        {
            SessionId = "",
            Message = new ChatMessage { Role = ChatRole.Assistant, Content = content }
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("WaitUntil condition not met");
            await Task.Delay(20);
        }
    }

    private sealed class TaskToolFixture : IDisposable
    {
        public string ParentId { get; } = "parent-1";
        public SessionData Child { get; }
        public Mock<ISessionManager> SessionManager { get; }
        public Mock<IAgentRegistry> AgentRegistry { get; }
        public Mock<IAgentLoopScheduler> LoopScheduler { get; }
        public ExecutionJobService ExecService { get; }
        public ServiceProvider ToolProvider { get; }
        public ConcurrentBag<(string SessionId, string Text, IDictionary<string, string>? Metadata)> SyntheticInvocations { get; } = new();
        private readonly ServiceProvider _provider;

        public TaskToolFixture(IAgentExecutor? executor = null, AgentDefinition? agent = null)
        {
            var agentDef = agent ?? new AgentDefinition
            {
                Name = "explore",
                Mode = AgentMode.SubAgent,
                Runtime = AgentRuntime.Native,
                Description = "explore agent"
            };

            Child = new SessionData
            {
                Id = "child-1",
                Kind = SessionKind.SubAgent,
                ParentSessionId = ParentId,
                SelectedAgent = "explore",
                SelectedModel = string.Empty
            };

            SessionManager = new Mock<ISessionManager>();
            SessionManager.Setup(s => s.CreateChildAsync(
                    ParentId,
                    agentDef.Name,
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<SessionPermissionRule>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Child);
            SessionManager.Setup(s => s.AddMessageAsync(
                    Child.Id,
                    It.IsAny<SessionMessage>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, SessionMessage, CancellationToken>((_, msg, _) => Child.AddMessage(msg))
                .Returns(Task.CompletedTask);
            SessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            SessionManager.Setup(s => s.Get(ParentId)).Returns((SessionData?)null);
            SessionManager.Setup(s => s.Get(Child.Id)).Returns(Child);
            SessionManager.Setup(s => s.LoadAsync(It.IsAny<string>())).ReturnsAsync((SessionData?)null);
            SessionManager.Setup(s => s.EnsureSessionAsync(
                    Child.Id,
                    It.IsAny<string?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(Child);

            AgentRegistry = new Mock<IAgentRegistry>();
            AgentRegistry.Setup(r => r.GetAgentAsync(agentDef.Name)).ReturnsAsync(agentDef);
            AgentRegistry.Setup(r => r.GetTaskableAgentsAsync()).ReturnsAsync(new List<AgentDefinition> { agentDef });

            LoopScheduler = new Mock<IAgentLoopScheduler>();
            LoopScheduler.Setup(l => l.IsLoopBusy(It.IsAny<string>())).Returns(false);
            LoopScheduler.Setup(l => l.InjectSyntheticAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IDictionary<string, string>?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, IDictionary<string, string>?, CancellationToken>(
                    (sid, text, meta, _) => SyntheticInvocations.Add((sid, text, meta)))
                .Returns(Task.CompletedTask);
            LoopScheduler.Setup(l => l.TryResumeWhenIdleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var instructionManager = new Mock<IInstructionManager>();
            instructionManager.Setup(m => m.InjectIfNeededAsync(
                    It.IsAny<SessionData>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InstructionInjectResult { Injected = false });

            var modelManager = new Mock<IModelManager>();
            modelManager.Setup(m => m.GetSessionModelRef(It.IsAny<SessionData>())).Returns(string.Empty);
            modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
                .Returns("m");
            modelManager.Setup(m => m.ResolveAcpModel(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);

            var runtimeManager = new Mock<IAgentRuntimeManager>();
            runtimeManager.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("general");

            var services = new ServiceCollection();
            services.AddSingleton(SessionManager.Object);
            services.AddSingleton(instructionManager.Object);
            services.AddSingleton(modelManager.Object);
            services.AddSingleton(AgentRegistry.Object);
            services.AddSingleton<IAgentExecutor>(executor ?? BuildExecutor());
            services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
            services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.WorkspaceRoot == "workspace-root"));
            services.AddSingleton(Mock.Of<ICommandRegistry>());
            _provider = services.BuildServiceProvider();

            var publisher = new ExecutionEventPublisher(
                new ExecutionOptions(),
                NullLogger<ExecutionEventPublisher>.Instance);

            ExecService = new ExecutionJobService(
                _provider,
                publisher,
                new ExecutionOptions(),
                Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == new SeeingAgentOptions()),
                NullLogger<ExecutionJobService>.Instance,
                new CompactionRunner(
                    new CompressionService(null!, Mock.Of<ISessionManager>()),
                    Mock.Of<IExecutionEventPublisher>(),
                    Mock.Of<ISessionManager>()));

            ToolProvider = new ServiceCollection()
                .AddSingleton(ExecService)
                .BuildServiceProvider();
        }

        public void Dispose()
        {
            ExecService.Dispose();
            ToolProvider.Dispose();
            _provider.Dispose();
        }
    }
}

/// <summary>
/// TaskStatusTool：查询执行引擎中的子会话执行状态；无执行记录时回落子会话消息。
/// </summary>
public class TaskStatusToolTests
{
    [Fact]
    public async Task ExecuteAsync_RunningTask_ShouldReturnRunning()
    {
        using var fixture = new TaskStatusToolFixture(executor: BlockingExecutor());
        await fixture.SubmitAsync();

        await WaitUntilAsync(() =>
            fixture.ExecService.GetOverview(fixture.Child.Id).CurrentExecution?.Status == ExecutionStatus.Running);

        var tool = new TaskStatusTool(NullLogger<TaskStatusTool>.Instance, fixture.SessionManager.Object);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { task_id = fixture.Child.Id }),
            new ToolContext { SessionId = fixture.ParentId, Services = fixture.ToolProvider });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain($"task_id: {fixture.Child.Id}");
        result.Output.Should().Contain("state: running");
    }

    [Fact]
    public async Task ExecuteAsync_CompletedWithoutActiveExecution_ShouldFallbackToSession()
    {
        using var fixture = new TaskStatusToolFixture(executor: BuildExecutor("done output"));
        await fixture.SubmitAsync();
        await fixture.WaitCompletedAsync();

        var tool = new TaskStatusTool(NullLogger<TaskStatusTool>.Instance, fixture.SessionManager.Object);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { task_id = fixture.Child.Id }),
            new ToolContext { SessionId = fixture.ParentId, Services = fixture.ToolProvider });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("state: completed");
        result.Output.Should().Contain("<task_result>");
        result.Output.Should().Contain("done output");
    }

    [Fact]
    public async Task ExecuteAsync_WaitTrue_ShouldWaitUntilCompleted()
    {
        using var fixture = new TaskStatusToolFixture(executor: BuildExecutor("waited output", delayMs: 400));
        await fixture.SubmitAsync();

        var tool = new TaskStatusTool(NullLogger<TaskStatusTool>.Instance, fixture.SessionManager.Object);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { task_id = fixture.Child.Id, wait = true, timeout_ms = 5000 }),
            new ToolContext { SessionId = fixture.ParentId, Services = fixture.ToolProvider });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("state: completed");
        result.Output.Should().Contain("<task_result>");
        result.Output.Should().Contain("waited output");
    }

    private static IAgentExecutor BuildExecutor(string content = "final answer", int delayMs = 0)
    {
        var mock = new Mock<IAgentExecutor>();
        mock.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken ct)
                => AssistantStream(content, delayMs, ct));
        return mock.Object;
    }

    private static IAgentExecutor BlockingExecutor()
    {
        var mock = new Mock<IAgentExecutor>();
        mock.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken ct)
                => BlockingStream(ct));
        return mock.Object;
    }

    private static async IAsyncEnumerable<IMessageEvent> AssistantStream(
        string content, int delayMs, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs, ct);
        yield return new StreamCompleteEvent
        {
            SessionId = "",
            Message = new ChatMessage { Role = ChatRole.Assistant, Content = content }
        };
    }

    private static async IAsyncEnumerable<IMessageEvent> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(10000, ct);
        yield break;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("WaitUntil condition not met");
            await Task.Delay(20);
        }
    }

    private sealed class TaskStatusToolFixture : IDisposable
    {
        public string ParentId { get; } = "parent-1";
        public SessionData Child { get; }
        public Mock<ISessionManager> SessionManager { get; }
        public ExecutionJobService ExecService { get; }
        public ServiceProvider ToolProvider { get; }
        private readonly ServiceProvider _provider;

        public TaskStatusToolFixture(IAgentExecutor? executor = null)
        {
            var agentDef = new AgentDefinition
            {
                Name = "explore",
                Mode = AgentMode.SubAgent,
                Runtime = AgentRuntime.Native
            };

            Child = new SessionData
            {
                Id = "child-1",
                Kind = SessionKind.SubAgent,
                ParentSessionId = ParentId,
                SelectedAgent = "explore",
                SelectedModel = string.Empty
            };

            SessionManager = new Mock<ISessionManager>();
            SessionManager.Setup(s => s.Get(ParentId)).Returns((SessionData?)null);
            SessionManager.Setup(s => s.Get(Child.Id)).Returns(Child);
            SessionManager.Setup(s => s.LoadAsync(Child.Id)).ReturnsAsync(Child);
            SessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            SessionManager.Setup(s => s.EnsureSessionAsync(
                    Child.Id,
                    It.IsAny<string?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(Child);

            var agentRegistry = new Mock<IAgentRegistry>();
            agentRegistry.Setup(r => r.GetAgentAsync(agentDef.Name)).ReturnsAsync(agentDef);

            var instructionManager = new Mock<IInstructionManager>();
            instructionManager.Setup(m => m.InjectIfNeededAsync(
                    It.IsAny<SessionData>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InstructionInjectResult { Injected = false });

            var modelManager = new Mock<IModelManager>();
            modelManager.Setup(m => m.GetSessionModelRef(It.IsAny<SessionData>())).Returns(string.Empty);
            modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
                .Returns("m");
            modelManager.Setup(m => m.ResolveAcpModel(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);

            var runtimeManager = new Mock<IAgentRuntimeManager>();
            runtimeManager.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("general");

            var services = new ServiceCollection();
            services.AddSingleton(SessionManager.Object);
            services.AddSingleton(instructionManager.Object);
            services.AddSingleton(modelManager.Object);
            services.AddSingleton(agentRegistry.Object);
            services.AddSingleton<IAgentExecutor>(executor ?? BuildExecutor());
            services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
            services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.WorkspaceRoot == "workspace-root"));
            services.AddSingleton(Mock.Of<ICommandRegistry>());
            _provider = services.BuildServiceProvider();

            var publisher = new ExecutionEventPublisher(
                new ExecutionOptions(),
                NullLogger<ExecutionEventPublisher>.Instance);

            ExecService = new ExecutionJobService(
                _provider,
                publisher,
                new ExecutionOptions(),
                Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == new SeeingAgentOptions()),
                NullLogger<ExecutionJobService>.Instance,
                new CompactionRunner(
                    new CompressionService(null!, Mock.Of<ISessionManager>()),
                    Mock.Of<IExecutionEventPublisher>(),
                    Mock.Of<ISessionManager>()));

            ToolProvider = new ServiceCollection()
                .AddSingleton(ExecService)
                .BuildServiceProvider();
        }

        public Task SubmitAsync()
        {
            return ExecService.SubmitAsync(
                Child.Id,
                ChatInput.FromText("hi"),
                new ChatOptions
                {
                    AgentId = "explore",
                    SkipUserMessagePersist = true,
                    SkipInstructionInject = true
                });
        }

        public async Task WaitCompletedAsync()
        {
            await WaitUntilAsync(() =>
            {
                var overview = ExecService.GetOverview(Child.Id);
                return overview.CurrentExecution == null && overview.QueueLength == 0;
            });
        }

        public void Dispose()
        {
            ExecService.Dispose();
            ToolProvider.Dispose();
            _provider.Dispose();
        }
    }
}
