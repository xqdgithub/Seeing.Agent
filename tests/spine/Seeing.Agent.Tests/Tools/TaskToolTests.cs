using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Tools;
using Seeing.Session.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// TaskTool：子会话 = 普通会话，创建子会话 + 经 <see cref="ISessionRuntime.DeliverAsync"/> 投递执行。
/// </summary>
public class TaskToolTests
{
    [Fact]
    public async Task ExecuteAsync_Foreground_ShouldDeliverExecutionAndReturnCompleted()
    {
        using var fixture = new TaskToolFixture(childResult: "final answer");
        var tool = fixture.CreateTaskTool();
        var context = new ToolContext { SessionId = fixture.ParentId, CallId = "call-1" };

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

        result.Metadata[TaskMetadataKeys.TaskId].Should().Be(fixture.Child.Id);
        result.Metadata[TaskMetadataKeys.TaskAgent].Should().Be("explore");
        result.Metadata[TaskMetadataKeys.TaskDescription].Should().Be("explore auth");
        result.Metadata[TaskMetadataKeys.TaskBackground].Should().Be(false);

        fixture.Runtime.Delivered.Should().Contain(m => m.TargetSessionId == fixture.Child.Id);
        fixture.Runtime.Overview(fixture.Child.Id).HasActiveExecution.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Background_ShouldReturnRunningAndNotifyParentWhenCompleted()
    {
        using var fixture = new TaskToolFixture(childResult: "background result");
        var tool = fixture.CreateTaskTool();
        var context = new ToolContext { SessionId = fixture.ParentId, CallId = "call-1" };

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

        await WaitUntilAsync(() => fixture.Runtime.Delivered.Any(m => m.TargetSessionId == fixture.ParentId));

        var notice = fixture.Runtime.Delivered.Last(m => m.TargetSessionId == fixture.ParentId);
        notice.Body.Should().Contain("后台任务已完成");
        notice.Body.Should().Contain($"task_id: {fixture.Child.Id}");
        notice.Body.Should().Contain("state: completed");
        notice.Body.Should().Contain("<task_result>");
        notice.Body.Should().Contain("background result");
    }

    [Fact]
    public async Task ExecuteAsync_Background_WhenChildCascadedStopped_ShouldNotNotifyParent()
    {
        using var fixture = new TaskToolFixture(childResult: "x");
        // 子因父停止级联停止：子会话带 stop_reason 标记
        fixture.Child.Metadata[SessionControlKeys.StopReason] = SessionControlKeys.StopReasonCascadedFromParent;
        var tool = fixture.CreateTaskTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth config",
                subagent_type = "explore",
                background = true
            }),
            new ToolContext { SessionId = fixture.ParentId, CallId = "call-cancel" });

        result.Success.Should().BeTrue();

        // 给后台 watcher 收尾留出窗口
        await Task.Delay(500);

        fixture.Runtime.Delivered.Should().NotContain(m => m.TargetSessionId == fixture.ParentId);
    }

    [Fact]
    public async Task ExecuteAsync_CreateChild_ShouldPassParentScenario()
    {
        using var fixture = new TaskToolFixture(childResult: "ok");
        var parent = new SessionData
        {
            Id = fixture.ParentId,
            Kind = SessionKind.Root,
            Scenario = "code"
        };
        fixture.SessionManager.Setup(s => s.GetOrLoadAsync(fixture.ParentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);

        string? capturedScenario = "unset";
        fixture.GroupManager
            .Setup(g => g.CreateChildAsync(
                fixture.ParentId,
                "explore",
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<SessionPermissionRule>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<SessionPermissionRule>, string?, CancellationToken>(
                (_, _, _, _, scenario, _) => capturedScenario = scenario)
            .ReturnsAsync(fixture.Child);

        var tool = fixture.CreateTaskTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth",
                subagent_type = "explore"
            }),
            new ToolContext { SessionId = fixture.ParentId, CallId = "call-sc" });

        result.Success.Should().BeTrue();
        capturedScenario.Should().Be("code");
    }

    [Fact]
    public void TaskTool_ShouldNotReferenceExecutionJobService()
    {
        var root = FindRepoRoot();
        var sourcePath = Path.Combine(root, "src", "hosting", "Seeing.Agent.Hosting", "Tools", "TaskTool.cs");

        File.Exists(sourcePath).Should().BeTrue($"TaskTool source not found at {sourcePath}");

        var source = File.ReadAllText(sourcePath);
        source.Should().NotContain("ExecutionJobService");
        source.Should().NotContain("IExecutionSubmitter");
        source.Should().NotContain("GetService(typeof(");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    }

    [Fact]
    public async Task ExecuteAsync_Foreground_ShouldWriteOriginToolCallIdToChildMetadata()
    {
        using var fixture = new TaskToolFixture(childResult: "final answer");
        var tool = fixture.CreateTaskTool();
        var context = new ToolContext { SessionId = fixture.ParentId, CallId = "call-origin-1" };

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth config",
                subagent_type = "explore"
            }),
            context);

        result.Success.Should().BeTrue();
        fixture.Child.Metadata.Should()
            .ContainKey(SessionMetadataKeys.OriginToolCallId)
            .WhoseValue.Should().Be("call-origin-1");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAgent_ShouldReturnFailure()
    {
        using var fixture = new TaskToolFixture();
        var tool = fixture.CreateTaskTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "x",
                prompt = "y",
                subagent_type = "does-not-exist"
            }),
            new ToolContext { SessionId = fixture.ParentId });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未知的 Agent 类型");
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

    private sealed class TaskToolFixture : IDisposable
    {
        public string ParentId { get; } = "parent-1";
        public SessionData Child { get; }
        public Mock<ISessionManager> SessionManager { get; }
        public Mock<ISessionGroupManager> GroupManager { get; }
        public Mock<IAgentRegistry> AgentRegistry { get; }
        public FakeSessionRuntime Runtime { get; }
        public IExecutionEventPublisher EventPublisher { get; }

        public TaskToolFixture(string childResult = "final answer")
        {
            var agentDef = new AgentDefinition
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
                SelectedAgent = "explore",
                SelectedModel = string.Empty,
                Metadata = new Dictionary<string, string>()
            };

            SessionManager = new Mock<ISessionManager>();
            SessionManager.Setup(s => s.AddMessageAsync(
                    Child.Id, It.IsAny<SessionMessage>(), It.IsAny<CancellationToken>()))
                .Callback<string, SessionMessage, CancellationToken>((_, msg, _) => Child.AddMessage(msg))
                .Returns(Task.CompletedTask);
            SessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            SessionManager.Setup(s => s.Get(ParentId)).Returns((SessionData?)null);
            SessionManager.Setup(s => s.GetOrLoadAsync(ParentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionData { Id = ParentId, Kind = SessionKind.Root });
            SessionManager.Setup(s => s.Get(Child.Id)).Returns(Child);
            SessionManager.Setup(s => s.LoadAsync(It.IsAny<string>())).ReturnsAsync((SessionData?)null);

            GroupManager = new Mock<ISessionGroupManager>();
            GroupManager.Setup(g => g.CreateChildAsync(
                    ParentId,
                    agentDef.Name,
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<SessionPermissionRule>>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Child);
            GroupManager.Setup(g => g.GetParentAsync(Child.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ParentId);
            GroupManager.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SessionData> { Child });

            AgentRegistry = new Mock<IAgentRegistry>();
            AgentRegistry.Setup(r => r.GetAgentAsync(agentDef.Name)).ReturnsAsync(agentDef);
            AgentRegistry.Setup(r => r.GetTaskableAgentsAsync()).ReturnsAsync(new List<AgentDefinition> { agentDef });

            EventPublisher = Mock.Of<IExecutionEventPublisher>();
            Runtime = new FakeSessionRuntime(SessionManager.Object, Child, childResult);
        }

        public TaskTool CreateTaskTool() =>
            new(
                NullLogger<TaskTool>.Instance,
                SessionManager.Object,
                GroupManager.Object,
                AgentRegistry.Object,
                Runtime,
                EventPublisher);

        public void Dispose() { }
    }

    /// <summary>
    /// 会话运行时桩：投递子会话 user 消息时模拟执行完成（写入 assistant 结果并标记 idle），
    /// 记录所有投递供断言父会话通知。
    /// </summary>
    private sealed class FakeSessionRuntime : ISessionRuntime
    {
        private readonly ISessionManager _sessions;
        private readonly SessionData _child;
        private readonly string _childResult;

        public ConcurrentQueue<InboxMessage> Delivered { get; } = new();

        public FakeSessionRuntime(ISessionManager sessions, SessionData child, string childResult)
        {
            _sessions = sessions;
            _child = child;
            _childResult = childResult;
        }

        public async Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default)
        {
            Delivered.Enqueue(message);

            // 首次投递子会话（用户消息）：模拟执行产出 assistant 结果
            if (message.TargetSessionId == _child.Id
                && message.Presentation == InboxPresentation.User
                && !_child.GetActiveMessages().Any(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)))
            {
                await _sessions.AddMessageAsync(_child.Id, SessionMessage.AssistantMessage(_childResult), ct);
            }

            return InboxResult.Ok(messageId: "msg-1");
        }

        public SessionExecutionOverview Overview(string sessionId) => GetOverview(sessionId);

        public SessionExecutionOverview GetOverview(string sessionId)
        {
            if (sessionId != _child.Id)
                return new SessionExecutionOverview();

            var completed = _child.GetActiveMessages()
                .Any(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
            return new SessionExecutionOverview
            {
                LastExecution = completed
                    ? new ExecutionRecord { Status = ExecutionStatus.Completed }
                    : null
            };
        }

        public bool HasAnyActiveExecution() => false;

        public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> StopAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();

        public async IAsyncEnumerable<IMessageEvent> SubscribeEvents(
            string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    }
}

/// <summary>
/// TaskStatusTool：查询运行时中的子会话执行状态；无执行记录时回落子会话消息。
/// </summary>
public class TaskStatusToolTests
{
    [Fact]
    public async Task ExecuteAsync_RunningTask_ShouldReturnRunning()
    {
        var fixture = new TaskStatusToolFixture();
        fixture.SetOverview("child-1", new ExecutionRecord { Status = ExecutionStatus.Running });
        var tool = fixture.CreateTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { task_id = "child-1" }),
            new ToolContext { SessionId = "parent-1" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("task_id: child-1");
        result.Output.Should().Contain("state: running");
    }

    [Fact]
    public async Task ExecuteAsync_CompletedWithoutActiveExecution_ShouldFallbackToSession()
    {
        var fixture = new TaskStatusToolFixture();
        await fixture.AddAssistantAsync("done output");
        var tool = fixture.CreateTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { task_id = "child-1" }),
            new ToolContext { SessionId = "parent-1" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("state: completed");
        result.Output.Should().Contain("<task_result>");
        result.Output.Should().Contain("done output");
    }

    [Fact]
    public async Task ExecuteAsync_WaitTrue_ShouldWaitUntilCompleted()
    {
        var fixture = new TaskStatusToolFixture();
        fixture.SetOverview("child-1", new ExecutionRecord { Status = ExecutionStatus.Running });
        await fixture.AddAssistantAsync("waited output");
        // wait 后再查一次 overview：模拟执行已完成
        fixture.SetOverviewAfterWait("child-1", new ExecutionRecord { Status = ExecutionStatus.Completed });
        var tool = fixture.CreateTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { task_id = "child-1", wait = true, timeout_ms = 5000 }),
            new ToolContext { SessionId = "parent-1" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("state: completed");
        result.Output.Should().Contain("<task_result>");
        result.Output.Should().Contain("waited output");
    }

    [Fact]
    public async Task ExecuteAsync_FailedWithoutActiveExecution_ShouldReturnErrorState()
    {
        var fixture = new TaskStatusToolFixture();
        fixture.Child.Status = SessionStatus.Error;
        fixture.Child.Metadata[SessionMetadataKeys.LastError] = "boom";
        var tool = fixture.CreateTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { task_id = "child-1" }),
            new ToolContext { SessionId = "parent-1" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("state: error");
        result.Output.Should().Contain("boom");
    }

    private sealed class TaskStatusToolFixture
    {
        public string ParentId { get; } = "parent-1";
        public SessionData Child { get; }

        private readonly Dictionary<string, ExecutionRecord> _overview = new();
        private readonly Dictionary<string, ExecutionRecord> _afterWait = new();
        private readonly List<ExecutionRecord> _last = new();

        public TaskStatusToolFixture()
        {
            Child = new SessionData
            {
                Id = "child-1",
                Kind = SessionKind.SubAgent,
                SelectedAgent = "explore",
                SelectedModel = string.Empty,
                Metadata = new Dictionary<string, string>()
            };
        }

        public void SetOverview(string sessionId, ExecutionRecord record) => _overview[sessionId] = record;

        public void SetOverviewAfterWait(string sessionId, ExecutionRecord record) => _afterWait[sessionId] = record;

        public Task AddAssistantAsync(string content)
        {
            Child.AddMessage(SessionMessage.AssistantMessage(content));
            return Task.CompletedTask;
        }

        public TaskStatusTool CreateTool()
        {
            var runtime = new StubRuntime(this);
            var sessions = new Mock<ISessionManager>();
            sessions.Setup(s => s.Get(Child.Id)).Returns(Child);
            sessions.Setup(s => s.LoadAsync(Child.Id)).ReturnsAsync(Child);
            var groups = new Mock<ISessionGroupManager>();
            groups.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SessionData> { Child });

            return new TaskStatusTool(
                NullLogger<TaskStatusTool>.Instance,
                sessions.Object,
                groups.Object,
                runtime);
        }

        private sealed class StubRuntime(TaskStatusToolFixture owner) : ISessionRuntime
        {
            private bool _waited;

            public Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default) =>
                Task.FromResult(InboxResult.Ok());

            public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);

            public Task<int> StopAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);

            public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => Task.FromResult(0);

            public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default)
            {
                _waited = true;
                return Task.CompletedTask;
            }

            public SessionExecutionOverview GetOverview(string sessionId)
            {
                var source = _waited && owner._afterWait.TryGetValue(sessionId, out var after)
                    ? after
                    : owner._overview.GetValueOrDefault(sessionId);
                return new SessionExecutionOverview { CurrentExecution = source };
            }

            public bool HasAnyActiveExecution() => false;

            public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();

            public async IAsyncEnumerable<IMessageEvent> SubscribeEvents(
                string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
        }
    }
}
