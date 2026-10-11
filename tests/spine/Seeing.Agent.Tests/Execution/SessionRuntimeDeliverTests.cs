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
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// SessionRuntime.DeliverAsync + 消费循环语义（spec §6）：
/// Inject 不触发、Wake 触发、合并消费、单飞。
/// </summary>
public class SessionRuntimeDeliverTests
{
    /// <summary>假执行体：记录调用次数、收到的批次，并可阻塞以制造并发窗口。</summary>
    private sealed class FakeRunner : IExecutionRunner
    {
        private int _runCount;
        private int _active;
        private int _maxActive;

        public int RunCount => Volatile.Read(ref _runCount);
        public int MaxActive => Volatile.Read(ref _maxActive);

        public readonly List<InboxBatch> Batches = new();
        public Func<Task>? OnRunAsync { get; set; }

        public async Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            Interlocked.Increment(ref _runCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);
            lock (Batches)
                Batches.Add(batch);

            try
            {
                if (OnRunAsync is not null)
                    await OnRunAsync();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            record.Status = ExecutionStatus.Completed;
            return record;
        }

        private void UpdateMax(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maxActive)))
            {
                if (Interlocked.CompareExchange(ref _maxActive, value, current) == current)
                    break;
            }
        }
    }

    private static SessionRuntime CreateRuntime(
        out SessionManager sessionManager, out FakeRunner runner, out IExecutionEventPublisher publisher)
    {
        sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        runner = new FakeRunner();
        publisher = new RecordingPublisher();

        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton(publisher);
        var provider = services.BuildServiceProvider();

        return new SessionRuntime(
            runner,
            publisher,
            provider,
            new ExecutionOptions { MaxConcurrentExecutions = -1 },
            NullLogger<SessionRuntime>.Instance);
    }

    private static InboxMessage Msg(
        string sessionId,
        InboxDelivery delivery,
        InboxPresentation presentation = InboxPresentation.User,
        string body = "hello") => new()
    {
        TargetSessionId = sessionId,
        Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "test" },
        Body = body,
        Delivery = delivery,
        Presentation = presentation
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
    public async Task Inject_ShouldPersistWithoutTriggeringLoop()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var result = await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject));

        result.Success.Should().BeTrue();
        await Task.Delay(300);
        runner.RunCount.Should().Be(0);

        var session = sessionManager.Get("s1")!;
        session.Messages.Should().ContainSingle();
        session.Messages[0].Metadata![InboxMetadataKeys.Delivery].Should().Be("inject");
        session.Messages[0].Metadata![InboxMetadataKeys.Consumed].Should().Be(false);
    }

    [Fact]
    public async Task Wake_ShouldTriggerSingleRun()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var result = await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake));

        result.Success.Should().BeTrue();
        await WaitUntilAsync(() => runner.RunCount == 1);
        await Task.Delay(200);
        runner.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task InjectThreeThenWake_ShouldConsumeAllInOneRun()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "a"));
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "b"));
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Inject, body: "c"));
        await runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake, body: "wake"));

        await WaitUntilAsync(() => runner.RunCount >= 1);
        await Task.Delay(200);

        runner.RunCount.Should().Be(1);
        InboxBatch batch;
        lock (runner.Batches)
            batch = runner.Batches[0];
        batch.Inputs.Should().HaveCount(4);
        batch.HasUserInput.Should().BeTrue();
        batch.FirstUserText.Should().Be("a");

        // 本批全部置 consumed=true（单一清除点）
        var session = sessionManager.Get("s1")!;
        session.Messages.Where(m => m.Metadata?.ContainsKey(InboxMetadataKeys.Presentation) == true)
            .Should().OnlyContain(m => Equals(m.Metadata![InboxMetadataKeys.Consumed], true));
    }

    [Fact]
    public async Task ConcurrentWake_ShouldRunSingleFlight()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        // 阻塞首轮执行，确保并发 Deliver 全部落在同一活跃循环窗口内
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OnRunAsync = () => release.Task;

        const int n = 20;
        var delivers = Enumerable.Range(0, n)
            .Select(i => runtime.DeliverAsync(Msg("s1", InboxDelivery.Wake, body: $"m{i}")))
            .ToArray();
        await Task.WhenAll(delivers);

        await WaitUntilAsync(() => runner.RunCount >= 1);
        release.SetResult();

        await WaitUntilAsync(() => runner.RunCount >= 1);
        await Task.Delay(300);

        // 单飞：任何时刻并发执行体不超过 1
        runner.MaxActive.Should().Be(1);
    }

    [Fact]
    public async Task Notify_ShouldNotPersist_AndPublishNotice()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out var publisher);
        await sessionManager.EnsureSessionAsync("s1");

        var result = await runtime.DeliverAsync(
            Msg("s1", InboxDelivery.Notify, InboxPresentation.None, body: "notice"));

        result.Success.Should().BeTrue();
        await Task.Delay(200);
        runner.RunCount.Should().Be(0);
        sessionManager.Get("s1")!.Messages.Should().BeEmpty();
        ((RecordingPublisher)publisher).Events.Should().ContainSingle(e => e is InboxNoticeEvent);
    }

    [Fact]
    public async Task NonePresentation_Inject_ShouldPersistButNotEnterHistoryOrWake()
    {
        var runtime = CreateRuntime(out var sessionManager, out var runner, out _);
        await sessionManager.EnsureSessionAsync("s1");

        var result = await runtime.DeliverAsync(
            Msg("s1", InboxDelivery.Inject, InboxPresentation.None, body: "已取消"));

        result.Success.Should().BeTrue();
        await Task.Delay(300);
        runner.RunCount.Should().Be(0);

        var session = sessionManager.Get("s1")!;
        session.Messages.Should().ContainSingle();
        var msg = session.Messages[0];
        msg.Role.Should().Be(MessageRole.System);
        msg.Content.Should().Be("已取消");
        msg.Metadata![InboxMetadataKeys.Presentation].Should().Be("none");
        msg.Metadata![InboxMetadataKeys.Consumed].Should().Be(true);

        // 落盘后加载模拟：不进 LLM 历史
        var history = ExecutionRunner.BuildHistoryFromSession(session);
        history.Should().BeEmpty();
    }

    private sealed class RecordingPublisher : IExecutionEventPublisher
    {
        public readonly List<IMessageEvent> Events = new();

        public void Publish(string sessionId, IMessageEvent evt)
        {
            lock (Events)
                Events.Add(evt);
        }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(
            string sessionId, CancellationToken cancellationToken) => Empty();

        private static async IAsyncEnumerable<IMessageEvent> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId)
            => Array.Empty<IMessageEvent>();

        public void ClearBuffer(string sessionId) { }

        public void CompleteSession(string sessionId) { }
    }
}
