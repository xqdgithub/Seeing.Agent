using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Agent.Tests.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 执行终态闭合：<see cref="ExecutionRunner"/> 组装 <see cref="ExecutionOutcome"/>、
/// 经 <see cref="ExecutionCompleteEvent.Outcome"/> 发布，并扇出 <see cref="IExecutionOutcomeObserver"/>。
/// <para>
/// 重点锁定两处最易写错的语义：
/// ① <c>AgentName</c> 只在真正执行过 Agent 时有值（命令短路必须为 null）；
/// ② 步数 / 用量 / 耗时是**累计**值，不是最后一次 <see cref="LoopCompleteEvent"/> 的值。
/// </para>
/// </summary>
public class ExecutionOutcomeAssemblyTests
{
    // === 契约层：ExecutionRecord 字段变更 ===

    [Fact]
    public void ExecutionRecord_Should_Not_Expose_ProcessedMessageCount()
    {
        // 该字段全仓从未赋值（恒 0），按设计删除；若被恢复，这里应立刻变红。
        typeof(ExecutionRecord).GetProperty("ProcessedMessageCount").Should().BeNull();
    }

    [Fact]
    public void ExecutionRecord_Should_Default_AgentExecuted_To_False()
    {
        new ExecutionRecord().AgentExecuted.Should().BeFalse();
    }

    // === 终态事实组装 ===

    [Fact]
    public async Task RunOnce_Should_Report_AgentExecuted_True_For_Normal_Turn()
    {
        var fx = BuildRunner(agentName: "build");
        var record = Record("ses_1");

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        outcome.AgentExecuted.Should().BeTrue();
        outcome.AgentName.Should().Be("build");
        outcome.IsTopLevel.Should().BeTrue();
        outcome.ParentSessionId.Should().BeNull();
        outcome.Status.Should().Be(ExecutionStatus.Completed);

        // 记录本身也必须带上同样的事实：其它消费方（SessionRuntime.BuildOutcome）从记录读。
        record.AgentExecuted.Should().BeTrue();
        record.AgentName.Should().Be("build");
    }

    [Fact]
    public async Task RunOnce_Should_Report_AgentExecuted_False_For_Command_Short_Circuit()
    {
        // 命令短路：命令消耗了本轮，Agent 从未被调用，不得出现「执行过某 Agent」的伪事实。
        var fx = BuildRunner(agentName: "build", commandRegistry: CreateShortCircuitCommandRegistry("clear"));
        var record = Record("ses_2", inputText: "/clear");

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        outcome.AgentExecuted.Should().BeFalse();
        outcome.AgentName.Should().BeNull("命令短路没有执行 Agent，名称必须为空");
        outcome.TotalSteps.Should().Be(0);
        outcome.Usage.Should().BeNull();

        fx.Executor.Verify(e => e.ExecuteAsync(
            It.IsAny<AgentDefinition>(),
            It.IsAny<IReadOnlyList<ChatMessage>>(),
            It.IsAny<AgentContext>(),
            It.IsAny<CancellationToken>()), Times.Never, "短路命令不得调用 Agent 执行器");

        record.AgentName.Should().BeNull();
        record.AgentExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task RunOnce_Should_Report_IsTopLevel_False_For_SubAgent_Session()
    {
        var fx = BuildRunner(
            agentName: "explore",
            sessionKind: SessionKind.SubAgent,
            parentSessionId: "ses_root");
        var record = Record("ses_child");

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        outcome.IsTopLevel.Should().BeFalse();
        outcome.ParentSessionId.Should().Be("ses_root");
    }

    [Fact]
    public async Task RunOnce_Should_Capture_Usage_And_TotalSteps_From_LoopComplete()
    {
        // 多段循环：步数/用量/耗时必须累计（第一段无用量，取第二段后再与第三段相加）。
        //
        // ⚠ 关于 ReasoningTokens 的重要说明：本用例用**手工构造**的 LoopCompleteEvent 验证
        // ExecutionRunner.MergeUsage 的逐字段累加语义；它**不**代表端到端该字段可用。
        // 上游 AgentExecutor 的 totalUsage 累加只写 InputTokens/OutputTokens
        //（首段构造时也不抄 ReasoningTokens，后续 with 只加 delta 的 input/output），
        // 因此真实链路里 LoopCompleteEvent.Usage.ReasoningTokens 出来就已是 0，
        // outcome.Usage.ReasoningTokens 端到端恒为 0。这是既有的独立缺陷，不在本任务范围内。
        var fx = BuildRunner(
            agentName: "build",
            loopEvents:
            [
                new LoopCompleteEvent
                {
                    SessionId = "ses_3", LoopId = "l1", TotalSteps = 1,
                    Duration = TimeSpan.FromSeconds(1), Success = true
                },
                new LoopCompleteEvent
                {
                    SessionId = "ses_3", LoopId = "l2", TotalSteps = 3,
                    Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
                    Duration = TimeSpan.FromSeconds(1), Success = true
                },
                new LoopCompleteEvent
                {
                    SessionId = "ses_3", LoopId = "l3", TotalSteps = 4,
                    Usage = new TokenUsage { InputTokens = 200, OutputTokens = 70, ReasoningTokens = 10 },
                    Duration = TimeSpan.FromSeconds(1), Success = true
                }
            ]);
        var record = Record("ses_3");

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        outcome.TotalSteps.Should().Be(8, "步数应逐次累加而不是取最后一次");
        outcome.Usage.Should().NotBeNull();
        outcome.Usage!.InputTokens.Should().Be(300);
        outcome.Usage.OutputTokens.Should().Be(120);
        outcome.Usage.ReasoningTokens.Should().Be(10, "仅证明 MergeUsage 逐字段累加；上游该字段恒 0");
        outcome.Usage.TotalTokens.Should().Be(420);
        outcome.Duration.Should().Be(TimeSpan.FromSeconds(3));

        record.TotalSteps.Should().Be(8);
        record.Usage!.TotalTokens.Should().Be(420);
    }

    [Fact]
    public async Task RunOnce_Should_Capture_Partial_Facts_From_LoopCancelled()
    {
        // 取消路径**不产出** LoopCompleteEvent（AgentExecutor 的 OCE 分支只写 LoopCancelledEvent），
        // 若只认 LoopCompleteEvent，取消轮次的步数与用量会全部记为 0 —— 等于丢掉「取消前已烧掉的成本」。
        var fx = BuildRunner(
            agentName: "build",
            loopEvents:
            [
                new LoopCancelledEvent
                {
                    SessionId = "ses_cancel",
                    LoopId = "l1",
                    Reason = "user",
                    CompletedSteps = 2,
                    PartialUsage = new TokenUsage { InputTokens = 40, OutputTokens = 9 }
                }
            ]);
        var record = Record("ses_cancel");

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        outcome.TotalSteps.Should().Be(2, "取消前已完成的步数必须保留");
        outcome.Usage.Should().NotBeNull();
        outcome.Usage!.InputTokens.Should().Be(40);
        outcome.Usage.OutputTokens.Should().Be(9);
        outcome.Usage.TotalTokens.Should().Be(49);
        outcome.Duration.Should().Be(TimeSpan.Zero, "LoopCancelledEvent 不携带耗时，不得臆造");
    }

    [Fact]
    public async Task RunOnce_Should_Not_Double_Count_When_Both_Terminal_Events_Arrive()
    {
        // 负向护栏：同一 LoopId 的终态事实只能计一次。
        // 两个终态事件是两个独立循环迭代 —— `else if` 只保证单次迭代内互斥，**挡不住双计**，
        // 必须靠 LoopId 去重集合（本用例就是这条去重的唯一护栏）。
        var fx = BuildRunner(
            agentName: "build",
            loopEvents:
            [
                new LoopCompleteEvent
                {
                    SessionId = "ses_double", LoopId = "l1", TotalSteps = 3,
                    Usage = new TokenUsage { InputTokens = 20, OutputTokens = 10 },
                    Duration = TimeSpan.FromSeconds(1), Success = true
                },
                new LoopCancelledEvent
                {
                    SessionId = "ses_double", LoopId = "l1", Reason = "user",
                    CompletedSteps = 2,
                    PartialUsage = new TokenUsage { InputTokens = 30, OutputTokens = 20 }
                }
            ]);
        var record = Record("ses_double");

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        outcome.TotalSteps.Should().Be(3, "同一 LoopId 的终态事实只计一次，不得把两个事件都累加");
        outcome.Usage!.TotalTokens.Should().Be(30);
        outcome.Duration.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RunOnce_Should_Accumulate_Distinct_Loop_Ids()
    {
        // 反向护栏：去重只能按 LoopId，不能退化成「全局只认第一个终态事件」。
        // 两个不同 LoopId 的终态事实必须都计入，否则多段执行会被系统性低估。
        var fx = BuildRunner(
            agentName: "build",
            loopEvents:
            [
                new LoopCompleteEvent
                {
                    SessionId = "ses_multi", LoopId = "l1", TotalSteps = 2,
                    Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 },
                    Duration = TimeSpan.FromSeconds(1), Success = true
                },
                new LoopCompleteEvent
                {
                    SessionId = "ses_multi", LoopId = "l2", TotalSteps = 3,
                    Usage = new TokenUsage { InputTokens = 20, OutputTokens = 10 },
                    Duration = TimeSpan.FromSeconds(2), Success = true
                }
            ]);
        var record = Record("ses_multi");

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        outcome.TotalSteps.Should().Be(5);
        outcome.Usage!.TotalTokens.Should().Be(45);
        outcome.Duration.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task RunOnce_Should_Stamp_Outcome_CompletedAt_From_Record()
    {
        // 时序闸门：finally 先写 record.CompletedAt，再经 FlushAsync 停留 80ms，最后组装 outcome。
        // 若实现漏掉 CompletedAt = record.CompletedAt，契约默认值（构造时刻）会比记录早 80ms 以上。
        var fx = BuildRunner(agentName: "build", onFlush: () => Task.Delay(80));
        var record = Record("ses_4");
        var runStartedAt = DateTime.UtcNow;

        await fx.Runner.RunOnceAsync(record, Batch(), CancellationToken.None);

        var outcome = fx.SingleOutcome();
        record.CompletedAt.Should().NotBeNull();
        outcome.CompletedAt.Should().Be(record.CompletedAt!.Value,
            "终态时刻必须取自记录，不能由契约默认值冒充");
        outcome.CompletedAt.Should().BeAfter(runStartedAt);
    }

    [Fact]
    public async Task RunOnce_Should_Fan_Out_Outcome_To_Observers_And_Isolate_Failures()
    {
        var throwing = new ThrowingObserver();
        var recording = new RecordingObserver();
        var filtered = new RecordingObserver { ObserveResult = false };

        var fx = BuildRunner(
            agentName: "build",
            observers: [throwing, recording, filtered]);

        await fx.Runner.RunOnceAsync(Record("ses_5"), Batch(), CancellationToken.None);

        // 抛异常的观察者不得阻断其它观察者，也不得影响执行链本身
        throwing.CallCount.Should().Be(1);
        recording.Outcomes.Should().ContainSingle();
        filtered.ShouldObserveCalls.Should().Be(1);
        filtered.Outcomes.Should().BeEmpty("ShouldObserve 为 false 时不得回调");

        // 观察者拿到的是与事件同一个终态实例（唯一真相，不做二次拼装）
        recording.Outcomes[0].Should().BeSameAs(fx.SingleOutcome());
    }

    [Fact]
    public async Task RunOnce_Should_Isolate_Async_Observer_Fault_After_Await()
    {
        // 关键路径：async 方法在 await 之后抛出时，异常落在返回的 Task 上（try/catch 包不住方法体），
        // 这正是「扇出必须 await 而非 `_ = ...`」的全部理由 —— 丢弃 Task 会让它变成未观察异常。
        // 本用例用真实 async + await 后 throw 覆盖该路径；同步 throw 由上一个用例覆盖。
        var asyncFaulting = new AsyncFaultingObserver();
        var recording = new RecordingObserver();

        var fx = BuildRunner(
            agentName: "build",
            observers: [asyncFaulting, recording]);

        await fx.Runner.RunOnceAsync(Record("ses_async_fault"), Batch(), CancellationToken.None);

        // 走到这里即证明：RunOnceAsync 未被该异常打断，且后续观察者仍被调用。
        asyncFaulting.ReachedAfterAwait.Should().BeTrue("观察者确实在 await 之后才抛");
        recording.Outcomes.Should().ContainSingle("异步抛出不得阻断后续观察者");
        fx.SingleOutcome().Status.Should().Be(ExecutionStatus.Completed, "观察者失败不得改变本轮终态");
    }

    [Fact]
    public async Task RunOnce_Should_Truncate_Slow_Observer_After_Timeout()
    {
        // 超时护栏：观察者契约要求毫秒级返回，慢实现不得拖住 RunOnceAsync 返回
        //（它连带阻塞 DeliverAndAwaitAsync 兑现与全局并发额度）。
        // 观察者挂 10 秒、超时设 100ms：断言「按超时放弃」，而不是等它自然结束。
        var slowTimeout = TimeSpan.FromMilliseconds(100);
        var slow = new SlowObserver(TimeSpan.FromSeconds(10));
        var recording = new RecordingObserver();

        var fx = BuildRunner(
            agentName: "build",
            observers: [slow, recording],
            observerTimeout: slowTimeout);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await fx.Runner.RunOnceAsync(Record("ses_slow"), Batch(), CancellationToken.None);
        elapsed.Stop();

        recording.Outcomes.Should().ContainSingle("超时的观察者被放弃后，后续观察者必须照常收到通知");
        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(slowTimeout, "必须等满超时上限才放弃该观察者");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "必须在超时后立刻继续，而不是等慢观察者自然结束（10s）");
        fx.SingleOutcome().Status.Should().Be(ExecutionStatus.Completed, "观察者超时不得改变本轮终态");
    }

    [Fact]
    public void ObserverTimeout_Default_Should_Be_Five_Seconds()
    {
        // 契约文档承诺「超过 5 秒会被截断」，此处锁住这个数字，避免文档与实现漂移。
        ExecutionRunner.ObserverTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    // === 测试夹具 ===

    /// <summary>一次执行的装配结果（供断言执行器是否被调用、事件与记录是否一致）。</summary>
    private sealed class Fixture
    {
        public required ExecutionRunner Runner { get; init; }
        public required List<IMessageEvent> Published { get; init; }
        public required Mock<IAgentExecutor> Executor { get; init; }

        /// <summary>取本轮唯一的 <see cref="ExecutionCompleteEvent"/> 及其携带的终态快照。</summary>
        public ExecutionOutcome SingleOutcome() =>
            Published.OfType<ExecutionCompleteEvent>().Should().ContainSingle().Subject.Outcome;
    }

    private static Fixture BuildRunner(
        string agentName = "build",
        SessionKind sessionKind = SessionKind.Root,
        string? parentSessionId = null,
        IReadOnlyList<IMessageEvent>? loopEvents = null,
        Mock<ICommandRegistry>? commandRegistry = null,
        IReadOnlyList<IExecutionOutcomeObserver>? observers = null,
        Func<Task>? onFlush = null,
        TimeSpan? observerTimeout = null)
    {
        var session = SessionData.Create();
        session.Kind = sessionKind;
        // 执行链经 AgentSelectionResolver 解析 Agent：记录未指定 AgentId 时回落到会话选择值，
        // 避免依赖进程级「默认 Agent」配置（测试不注册该配置）。
        session.SelectedAgent = agentName;

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.EnsureSessionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns(session);
        sessionManager.Setup(m => m.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        sessionManager
            .Setup(m => m.FlushAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => onFlush?.Invoke() ?? Task.CompletedTask);

        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => StreamOf(loopEvents ?? []));

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry
            .Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = agentName, Runtime = AgentRuntime.Native });

        // 会话组：父会话命中与否由测试给定（TryGetParent 只查内存索引）
        var groupManager = new Mock<ISessionGroupManager>();
        groupManager
            .Setup(g => g.TryGetParent(It.IsAny<string>(), out It.Ref<string?>.IsAny))
            .Returns((string _, out string? parent) =>
            {
                parent = parentSessionId;
                return parentSessionId is not null;
            });

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(CreateModelManager());
        services.AddSingleton(executor.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(new AgentSelectionResolver(Mock.Of<IAgentRuntimeManager>()));
        services.AddSingleton(CreateWorkspaceProvider());
        services.AddSingleton(CreateExecutionWorld());
        services.AddSingleton(commandRegistry?.Object ?? Mock.Of<ICommandRegistry>());
        services.AddSingleton(groupManager.Object);
        services.AddSingleton<ISessionToolExposureWriter>(new SessionToolExposureWriter(sessionManager.Object));
        services.AddStubContextPipeline();

        foreach (var observer in observers ?? [])
            services.AddSingleton<IExecutionOutcomeObserver>(observer);

        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore
            .Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        var published = new List<IMessageEvent>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher
            .Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback<string, IMessageEvent>((_, evt) => published.Add(evt));

        var runner = new ExecutionRunner(
            provider,
            publisher.Object,
            new ExecutionOptions(),
            configStore.Object,
            new CompactionRunner(
                new CompressionService(null!, sessionManager.Object),
                Mock.Of<IExecutionEventPublisher>(),
                sessionManager.Object),
            NullLogger<ExecutionRunner>.Instance,
            new StubSessionToolSchemaResolver());

        if (observerTimeout is { } timeout)
            runner.OverrideObserverTimeoutForTest(timeout);

        return new Fixture { Runner = runner, Published = published, Executor = executor };
    }

    /// <summary>模型管理器替身：解析结果固定为 null（不触发真实模型目录）。</summary>
    private static IModelManager CreateModelManager()
    {
        var modelManager = new Mock<IModelManager>();
        modelManager
            .Setup(m => m.ResolveNativeModel(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((string?)null);
        modelManager
            .Setup(m => m.ApplyModelToSession(It.IsAny<SessionData>(), It.IsAny<string>()))
            .Returns(false);
        return modelManager.Object;
    }

    /// <summary>工作区提供者替身：项目根经 <c>ProjectSeeingDirectory</c> 推导为 <c>workspace-root</c>。</summary>
    private static IWorkspaceProvider CreateWorkspaceProvider()
    {
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.ProjectSeeingDirectory)
            .Returns(Path.Combine("workspace-root", ".seeing"));
        return workspace.Object;
    }

    /// <summary>执行世界替身：cwd 固定为 <c>workspace-root</c>。</summary>
    private static IExecutionWorld CreateExecutionWorld()
    {
        var world = new Mock<IExecutionWorld>();
        world.SetupGet(w => w.Cwd).Returns("workspace-root");
        return world.Object;
    }

    private static ExecutionRecord Record(string sessionId, string? inputText = "hello") => new()
    {
        ExecutionId = "exec_1",
        SessionId = sessionId,
        Status = ExecutionStatus.Running,
        Input = new ChatInput { Text = inputText },
        Options = new ChatOptions { SkipInstructionInject = true }
    };

    private static InboxBatch Batch() =>
        new([SessionMessage.UserMessage("hello")], HasUserInput: false, FirstUserText: null);

    /// <summary>构造一个「执行后要求短路本轮」的命令注册表（shouldContinue=false）。</summary>
    private static Mock<ICommandRegistry> CreateShortCircuitCommandRegistry(string commandName)
    {
        var command = new Mock<ICommand>();
        command.Setup(c => c.Metadata)
            .Returns(CommandMetadata.Simple(commandName, "测试命令", category: CommandCategory.Basic));
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("done", shouldContinue: false));

        var registry = new Mock<ICommandRegistry>();
        registry.Setup(r => r.GetCommand(commandName, AgentRuntime.Native)).Returns(command.Object);
        return registry;
    }

    private static async IAsyncEnumerable<IMessageEvent> StreamOf(IReadOnlyList<IMessageEvent> events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }

    /// <summary>回调必抛异常的观察者：验证扇出不因单点失败而中断。</summary>
    private sealed class ThrowingObserver : IExecutionOutcomeObserver
    {
        public int CallCount { get; private set; }

        public bool ShouldObserve(ExecutionOutcome outcome) => true;

        public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("观察者故意失败");
        }
    }

    /// <summary>记录被调用情况的观察者；<see cref="ObserveResult"/> 为 false 时模拟过滤。</summary>
    private sealed class RecordingObserver : IExecutionOutcomeObserver
    {
        public bool ObserveResult { get; init; } = true;

        public int ShouldObserveCalls { get; private set; }

        public List<ExecutionOutcome> Outcomes { get; } = [];

        public bool ShouldObserve(ExecutionOutcome outcome)
        {
            ShouldObserveCalls++;
            return ObserveResult;
        }

        public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    /// <summary>在 <c>await</c> 之后抛异常的观察者：覆盖「异常落在 Task 上」的真实路径。</summary>
    private sealed class AsyncFaultingObserver : IExecutionOutcomeObserver
    {
        /// <summary>是否已越过 <c>await</c>（用于证明异常确实发生在异步续体里）。</summary>
        public bool ReachedAfterAwait { get; private set; }

        public bool ShouldObserve(ExecutionOutcome outcome) => true;

        public async Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
        {
            await Task.Yield();
            ReachedAfterAwait = true;
            throw new InvalidOperationException("异步续体中故意失败");
        }
    }

    /// <summary>挂起指定时长才返回的观察者：验证超时截断。</summary>
    private sealed class SlowObserver : IExecutionOutcomeObserver
    {
        private readonly TimeSpan _delay;

        public SlowObserver(TimeSpan delay) => _delay = delay;

        public bool ShouldObserve(ExecutionOutcome outcome) => true;

        public async Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // 不用 ct：验证的是宿主侧超时，而不是协作式取消。
            using var timer = new Timer(_ => tcs.TrySetResult(), null, _delay, Timeout.InfiniteTimeSpan);
            await tcs.Task.ConfigureAwait(false);
        }
    }
}
