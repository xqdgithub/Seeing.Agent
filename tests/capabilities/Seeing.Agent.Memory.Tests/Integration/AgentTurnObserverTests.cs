using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

/// <summary>
/// <see cref="AgentTurnMemoryHandler"/> 作为执行终态观察者的行为锁定。
/// <para>
/// 历史缺陷：该处理器曾是 <c>agent.after_invoke</c> 钩子处理器，但该钩子点在全仓没有任何
/// <c>Trigger*</c> 调用点，导致 <c>Extraction.ExtractEveryNTurns</c> 配置从未生效。
/// 本测试锁定「改由 <see cref="IExecutionOutcomeObserver"/> 承载」后的门禁口径与触发行为。
/// </para>
/// </summary>
public class AgentTurnObserverTests
{
    /// <summary>
    /// 门禁的完整覆盖：任一事实不满足即不得观察本轮。
    /// </summary>
    [Theory]
    // agentExecuted, isTopLevel, origin, status, sessionId, extractionEnabled, everyNTurns, expected
    [InlineData(true, true, SessionOrigin.User, ExecutionStatus.Completed, "s", true, 10, true)]       // 正常顶层完成轮
    [InlineData(false, true, SessionOrigin.User, ExecutionStatus.Completed, "s", true, 10, false)]     // 命令短路 / 纯系统批次
    [InlineData(true, false, SessionOrigin.User, ExecutionStatus.Completed, "s", true, 10, false)]     // 子代理会话
    // 人造组合：真实链路中 Origin=Reflection 的会话只能经 CreateInternalAsync → CreateChildCoreAsync 创建，
    // 那里硬性 Kind=SubAgent → IsTopLevel=false，故本条已由「子代理会话」那条覆盖。
    // 此处保留是为了锁定「若将来 IsTopLevel 语义按 spec §4.2 修正为 true，反射门禁仍独立生效」。
    [InlineData(true, true, SessionOrigin.Reflection, ExecutionStatus.Completed, "s", true, 10, false)]
    [InlineData(true, true, SessionOrigin.User, ExecutionStatus.Failed, "s", true, 10, false)]         // 失败
    [InlineData(true, true, SessionOrigin.User, ExecutionStatus.Cancelled, "s", true, 10, false)]      // 中断
    [InlineData(true, true, SessionOrigin.User, ExecutionStatus.Completed, "s", false, 10, false)]     // 抽取总开关关闭
    [InlineData(true, true, SessionOrigin.User, ExecutionStatus.Completed, "s", true, 0, false)]       // 轮次触发关闭
    [InlineData(true, true, SessionOrigin.User, ExecutionStatus.Completed, "  ", true, 10, false)]     // 无会话 ID
    public void ShouldObserve_Should_Gate_On_ExecutionFacts(
        bool agentExecuted,
        bool isTopLevel,
        SessionOrigin origin,
        ExecutionStatus status,
        string sessionId,
        bool extractionEnabled,
        int everyNTurns,
        bool expected)
    {
        var handler = BuildHandler(extractionEnabled, everyNTurns);

        var outcome = new ExecutionOutcome
        {
            SessionId = sessionId,
            ExecutionId = "e",
            Status = status,
            AgentExecuted = agentExecuted,
            IsTopLevel = isTopLevel,
            Origin = origin,
        };

        handler.ShouldObserve(outcome).Should().Be(expected);
    }

    [Fact(DisplayName = "模块已停用时不得观察本轮（避免停用期 batch 积压）")]
    public void ShouldObserve_When_Module_Inactive_Should_Be_False()
    {
        var activity = new MemoryModuleActivity();
        activity.MarkInactiveAndWake();
        var handler = BuildHandler(Mock.Of<IMemoryFlushService>(), new MemoryOptions(), activity);

        handler.ShouldObserve(CompletedOutcome("s")).Should().BeFalse(
            "模块停用后 ExecutionRunner 仍会扇出观察者，若不门控会在 channel 里积压过期 batch");
    }

    [Fact(DisplayName = "模块未注入 activity 时视为活跃（测试/独立用例场景）")]
    public void ShouldObserve_When_Activity_Not_Injected_Should_Be_True()
    {
        var handler = BuildHandler(Mock.Of<IMemoryFlushService>(), new MemoryOptions());

        handler.ShouldObserve(CompletedOutcome("s")).Should().BeTrue(
            "null 表示未注入，不能把「无模块生命周期」误判为停用");
    }

    [Fact(DisplayName = "终态回调应触发一次轮次 flush")]
    public async Task OnExecutionCompleted_Should_Trigger_Flush()
    {
        var flush = new Mock<IMemoryFlushService>();
        flush.Setup(f => f.TryFlushAfterTurn("s")).Returns(true);
        var handler = BuildHandler(flush.Object);

        await handler.OnExecutionCompletedAsync(CompletedOutcome("s"), TestContext.Current.CancellationToken);

        flush.Verify(f => f.TryFlushAfterTurn("s"), Times.Once);
    }

    [Fact(DisplayName = "直接调用终态回调时亦不绕过门禁")]
    public async Task OnExecutionCompleted_When_Gate_Fails_Should_Not_Flush()
    {
        var flush = new Mock<IMemoryFlushService>(MockBehavior.Strict);
        var handler = BuildHandler(flush.Object, new MemoryOptions
        {
            Extraction = new MemoryExtractionOptions { Enabled = false, ExtractEveryNTurns = 10 }
        });

        await handler.OnExecutionCompletedAsync(CompletedOutcome("s"), TestContext.Current.CancellationToken);

        flush.Verify(f => f.TryFlushAfterTurn(It.IsAny<string>()), Times.Never,
            "门禁是单一真相源，直接调用者也不能绕过");
    }

    [Fact(DisplayName = "flush 抛异常时终态回调不得外抛")]
    public async Task OnExecutionCompleted_When_Flush_Throws_Should_Not_Throw()
    {
        var flush = new Mock<IMemoryFlushService>();
        flush.Setup(f => f.TryFlushAfterTurn(It.IsAny<string>()))
            .Throws(new InvalidOperationException("flush 内部故障"));
        var handler = BuildHandler(flush.Object);

        var act = () => handler.OnExecutionCompletedAsync(
            CompletedOutcome("s"), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("观察者契约要求自身失败不得影响执行链");
    }

    [Fact(DisplayName = "门禁判定自身抛异常时亦不得外抛")]
    public async Task OnExecutionCompleted_When_Gate_Throws_Should_Not_Throw()
    {
        // 门禁读热重载配置；配置源故障时 ShouldObserve 可能抛。
        // 类文档承诺「异常一律在内部消化」，故门禁必须在 try 内。
        var handler = new AgentTurnMemoryHandler(
            Mock.Of<IMemoryFlushService>(),
            new ThrowingOptionsMonitor<MemoryOptions>(new InvalidOperationException("配置源故障")),
            NullLogger<AgentTurnMemoryHandler>.Instance);

        var act = () => handler.OnExecutionCompletedAsync(
            CompletedOutcome("s"), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("门禁在 try 外会让配置异常直接击穿执行链");
    }

    [Fact(DisplayName = "DI 中观察者与具体处理器应解析为同一实例")]
    public void Di_Resolution_Should_Return_Same_Handler_Instance()
    {
        var services = BuildMemoryServices();

        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<AgentTurnMemoryHandler>();
        var observer = provider.GetRequiredService<IExecutionOutcomeObserver>();

        observer.Should().BeSameAs(concrete,
            "观察者必须以工厂方式指向同一单例，否则内部状态会在两个实例间分裂");
    }

    [Fact(DisplayName = "重复注册 AddMemoryServices 不得让观察者翻倍")]
    public void Di_Double_Registration_Should_Keep_Single_Observer()
    {
        var services = new ServiceCollection();
        AddMemoryTestStubs(services);
        // 宿主共享同一 registry：AddMemoryServices 会校验 registry 未被替换为不同实例
        var registry = new StubConfigSectionRegistry();
        services.AddMemoryServices(registry, "Data Source=:memory:");
        services.AddMemoryServices(registry, "Data Source=:memory:");

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IExecutionOutcomeObserver>().Should().HaveCount(1,
            "观察者翻倍会让 TryFlushAfterTurn 调两次，轮次计数与 flush 时机全部减半");
    }

    [Fact(DisplayName = "重复注册后观察者既唯一又与会具体类型同实例")]
    public void Di_Double_Registration_Should_Keep_Single_Shared_Instance()
    {
        var services = new ServiceCollection();
        AddMemoryTestStubs(services);
        var registry = new StubConfigSectionRegistry();
        services.AddMemoryServices(registry, "Data Source=:memory:");
        services.AddMemoryServices(registry, "Data Source=:memory:");

        using var provider = services.BuildServiceProvider();

        // 合并断言：单独测 HaveCount(1) 抓不到「唯一但与会具体类型分裂」，
        // 单独测单次注册的 BeSameAs 抓不到「不同注册路径各建一个实例」。
        var concrete = provider.GetRequiredService<AgentTurnMemoryHandler>();
        var observers = provider.GetServices<IExecutionOutcomeObserver>().ToList();

        observers.Should().ContainSingle("观察者翻倍会让 TryFlushAfterTurn 调两次，轮次计数与 flush 时机全部减半");
        observers[0].Should().BeSameAs(concrete,
            "重复注册后接口与具体类型仍须指向同一单例，否则内部状态会在两个实例间分裂");
    }

    private static ServiceCollection BuildMemoryServices()
    {
        var services = new ServiceCollection();
        AddMemoryTestStubs(services);
        services.AddMemoryServices(new StubConfigSectionRegistry(), "Data Source=:memory:");
        return services;
    }

    /// <summary>最小替身集：日志、配置存储、文件存储、LLM 补全端口。</summary>
    private static void AddMemoryTestStubs(IServiceCollection services)
    {
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfigSectionStore>(new StubConfigSectionStore());
        services.AddSingleton(Mock.Of<IFileStore>());
        // flush 链路最终会实例化 LlmMemoryExtractor（抽取器），需要 LLM 补全端口占位
        services.AddSingleton(Mock.Of<Seeing.Agent.Abstractions.Llm.ITextCompletion>());
    }

    /// <summary>读取 CurrentValue 即抛的配置监视器，用于验证门禁异常被隔离。</summary>
    private sealed class ThrowingOptionsMonitor<T>(Exception error) : IOptionsMonitor<T>
    {
        public T CurrentValue => throw error;

        public T Get(string? name) => throw error;

        public IDisposable OnChange(Action<T, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static ExecutionOutcome CompletedOutcome(string sessionId) => new()
    {
        SessionId = sessionId,
        ExecutionId = "e",
        Status = ExecutionStatus.Completed,
        AgentExecuted = true,
        IsTopLevel = true,
        Origin = SessionOrigin.User,
    };

    private static AgentTurnMemoryHandler BuildHandler(
        bool extractionEnabled = true,
        int everyNTurns = 10) =>
        BuildHandler(
            Mock.Of<IMemoryFlushService>(),
            new MemoryOptions
            {
                Extraction = new MemoryExtractionOptions
                {
                    Enabled = extractionEnabled,
                    ExtractEveryNTurns = everyNTurns,
                }
            });

    private static AgentTurnMemoryHandler BuildHandler(IMemoryFlushService flush) =>
        BuildHandler(
            flush,
            new MemoryOptions
            {
                Extraction = new MemoryExtractionOptions { Enabled = true, ExtractEveryNTurns = 10 }
            });

    private static AgentTurnMemoryHandler BuildHandler(
        IMemoryFlushService flush,
        MemoryOptions options,
        MemoryModuleActivity? activity = null) =>
        new(flush,
            MemoryTestOptions.Monitor(options),
            NullLogger<AgentTurnMemoryHandler>.Instance,
            activity);

    private sealed class StubConfigSectionStore : IConfigSectionStore
    {
        private readonly Dictionary<string, object> _sections = new(StringComparer.OrdinalIgnoreCase);

        public T GetSection<T>(string sectionName) where T : class, new() =>
            _sections.TryGetValue(sectionName, out var value) && value is T typed ? typed : new T();

        public Task SaveSectionAsync<T>(
            string sectionName,
            T value,
            ConfigLevel level = ConfigLevel.Project,
            CancellationToken ct = default) where T : class
        {
            _sections[sectionName] = value;
            return Task.CompletedTask;
        }

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
        {
            add { }
            remove { }
        }
    }
}
