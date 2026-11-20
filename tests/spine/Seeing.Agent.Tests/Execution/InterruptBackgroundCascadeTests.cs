using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// <see cref="ISessionRuntime.InterruptAsync"/> 的后台级联参数：
/// 默认必须**不**触碰后台活动，显式打开时必须把当前执行 Id 传给控制端口。
/// </summary>
public class InterruptBackgroundCascadeTests
{
    /// <summary>可阻塞的假执行体：让「当前执行」稳定存在，供中断命中。</summary>
    private sealed class BlockingRunner : IExecutionRunner
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            _ = batch;
            _ = executionToken;
            await _release.Task.ConfigureAwait(false);
            record.Status = ExecutionStatus.Completed;
            return record;
        }
    }

    private sealed class NoopPublisher : IExecutionEventPublisher
    {
        public void Publish(string sessionId, IMessageEvent evt) { }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(
            string sessionId, CancellationToken cancellationToken) => Stream();

        private static async IAsyncEnumerable<IMessageEvent> Stream()
        {
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => [];

        public void ClearBuffer(string sessionId) { }

        public void CompleteSession(string sessionId) { }

        public ILiveEventSubscription SubscribeLive(string sessionId) => LiveEventSubscription.Completed;
    }

    private static SessionRuntime CreateRuntime(
        out SessionManager sessionManager,
        out BlockingRunner runner,
        IBackgroundActivityControl control)
    {
        sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        runner = new BlockingRunner();

        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton(control);

        var provider = services.BuildServiceProvider();

        var options = new ExecutionOptions
        {
            MaxConcurrentExecutions = -1,
            SessionIdleTimeout = TimeSpan.FromMinutes(30),
            // 拉长清理间隔，避免定时器在测试期间自动触发
            CleanupInterval = TimeSpan.FromHours(1)
        };

        return new SessionRuntime(
            runner, new NoopPublisher(), provider, options, NullLogger<SessionRuntime>.Instance);
    }

    private static InboxMessage Msg(string sessionId) => new()
    {
        TargetSessionId = sessionId,
        Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "test" },
        Body = "hello",
        Delivery = InboxDelivery.Wake,
        Presentation = InboxPresentation.User
    };

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

    [Fact]
    public async Task InterruptAsync_Default_ShouldNotTouchBackgroundActivities()
    {
        var control = new RecordingBackgroundActivityControl();
        var runtime = CreateRuntime(out var sessionManager, out var runner, control);
        await sessionManager.EnsureSessionAsync("s1");

        await runtime.DeliverAsync(Msg("s1"));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);

        var interrupted = await runtime.InterruptAsync("s1");

        interrupted.Should().BeTrue();
        control.Calls.Should().BeEmpty("默认中断只停当前轮，不打断仍在跑的后台活动");

        runner.Release();
        await runtime.WaitForIdleAsync("s1");
    }

    [Fact]
    public async Task InterruptAsync_WithCascade_ShouldCallCancelByExecution()
    {
        var control = new RecordingBackgroundActivityControl();
        var runtime = CreateRuntime(out var sessionManager, out var runner, control);
        await sessionManager.EnsureSessionAsync("s1");

        await runtime.DeliverAsync(Msg("s1"));
        await WaitUntilAsync(() => runtime.GetOverview("s1").CurrentExecution is not null);
        var executionId = runtime.GetOverview("s1").CurrentExecution!.ExecutionId;
        executionId.Should().NotBeNullOrEmpty();

        var interrupted = await runtime.InterruptAsync("s1", cancelBackgroundActivities: true);

        interrupted.Should().BeTrue();
        control.Calls.Should().ContainSingle();
        control.Calls[0].ExecutionId.Should().Be(executionId);
        control.Calls[0].Reason.Should().Be(BackgroundActivityCancelReason.User);

        runner.Release();
        await runtime.WaitForIdleAsync("s1");
    }
}
