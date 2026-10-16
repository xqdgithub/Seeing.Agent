using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using System.Collections.Immutable;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// 和解加载时残留的「执行中」task 卡片：由 SessionRuntime.ReconcileIncompleteTasksAsync 负责。
/// </summary>
public class SessionRuntimeReconcileTests
{
    private sealed class FakeRunner : IExecutionRunner
    {
        public Func<Task>? OnRunAsync { get; set; }

        public async Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            if (OnRunAsync is not null)
                await OnRunAsync();
            record.Status = ExecutionStatus.Completed;
            return record;
        }
    }

    private sealed class RecordingPublisher : IExecutionEventPublisher
    {
        public void Publish(string sessionId, IMessageEvent evt) { }
        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(string sessionId, CancellationToken cancellationToken)
            => Empty();
        private static async IAsyncEnumerable<IMessageEvent> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();
        public void ClearBuffer(string sessionId) { }
        public void CompleteSession(string sessionId) { }
    }

    private static SessionRuntime CreateRuntime(
        SessionManager sessionManager, out FakeRunner runner)
    {
        runner = new FakeRunner();
        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton<IExecutionEventPublisher>(new RecordingPublisher());
        var provider = services.BuildServiceProvider();

        return new SessionRuntime(
            runner,
            new RecordingPublisher(),
            provider,
            new ExecutionOptions { MaxConcurrentExecutions = -1, CleanupInterval = TimeSpan.FromHours(1) },
            NullLogger<SessionRuntime>.Instance);
    }

    private static async Task<SessionData> CreateSessionWithTaskAsync(SessionManager sessionManager, string status)
    {
        var session = await sessionManager.EnsureSessionAsync("s-reconcile");
        var msg = SessionMessage.AssistantMessage("thinking");
        msg.ToolCalls = ImmutableList.Create(new SessionToolCall
        {
            Id = "t1",
            Name = "task",
            Status = status,
            TaskId = "child-orphan"
        });
        session.AddMessage(msg);
        return session;
    }

    [Fact]
    public async Task ReconcileIncompleteTasksAsync_NoActiveExecution_ShouldMarkCancelled()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var session = await CreateSessionWithTaskAsync(sessionManager, "running");

        var runtime = CreateRuntime(sessionManager, out _);

        var count = await runtime.ReconcileIncompleteTasksAsync(
            session.Id, TestContext.Current.CancellationToken);

        count.Should().Be(1);
        var tc = sessionManager.Get(session.Id)!.Messages[0].ToolCalls![0];
        tc.Status.Should().Be("cancelled");
        tc.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReconcileIncompleteTasksAsync_CompletedTask_ShouldNotTouch()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var session = await CreateSessionWithTaskAsync(sessionManager, "success");

        var runtime = CreateRuntime(sessionManager, out _);

        var count = await runtime.ReconcileIncompleteTasksAsync(
            session.Id, TestContext.Current.CancellationToken);

        count.Should().Be(0);
        sessionManager.Get(session.Id)!.Messages[0].ToolCalls![0].Status.Should().Be("success");
    }

    [Fact]
    public async Task ReconcileIncompleteTasksAsync_SessionStillExecuting_ShouldSkip()
    {
        // 会话自身仍在执行（前台 task 在途、TaskId 未回填）：不得误判为孤儿取消。
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var session = await CreateSessionWithTaskAsync(sessionManager, "running");

        var runtime = CreateRuntime(sessionManager, out var runner);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        await runtime.DeliverAsync(new InboxMessage
        {
            TargetSessionId = session.Id,
            Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "test" },
            Body = "hi",
            Delivery = InboxDelivery.Wake,
            Presentation = InboxPresentation.User
        });
        await WaitUntilAsync(() => runtime.GetOverview(session.Id).CurrentExecution is not null);

        var count = await runtime.ReconcileIncompleteTasksAsync(
            session.Id, TestContext.Current.CancellationToken);

        count.Should().Be(0);
        sessionManager.Get(session.Id)!.Messages[0].ToolCalls![0].Status.Should().Be("running");

        await runtime.StopAsync(session.Id);
        release.SetResult();
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
}
