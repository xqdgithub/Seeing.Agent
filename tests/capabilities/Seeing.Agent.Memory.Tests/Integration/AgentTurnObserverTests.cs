using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration;
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
    /// 六条门禁的完整覆盖：任一事实不满足即不得观察本轮。
    /// </summary>
    [Theory]
    // agentExecuted, isTopLevel, origin, status, sessionId, extractionEnabled, everyNTurns, expected
    [InlineData(true, true, SessionOrigin.User, ExecutionStatus.Completed, "s", true, 10, true)]       // 正常顶层完成轮
    [InlineData(false, true, SessionOrigin.User, ExecutionStatus.Completed, "s", true, 10, false)]     // 命令短路 / 纯系统批次
    [InlineData(true, false, SessionOrigin.User, ExecutionStatus.Completed, "s", true, 10, false)]     // 子代理会话
    [InlineData(true, true, SessionOrigin.Reflection, ExecutionStatus.Completed, "s", true, 10, false)] // 复盘会话自身
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

    [Fact(DisplayName = "终态回调应触发一次轮次 flush")]
    public async Task OnExecutionCompleted_Should_Trigger_Flush()
    {
        var flush = new Mock<IMemoryFlushService>();
        flush.Setup(f => f.TryFlushAfterTurn("s")).Returns(true);
        var handler = BuildHandler(flush.Object);

        await handler.OnExecutionCompletedAsync(CompletedOutcome("s"));

        flush.Verify(f => f.TryFlushAfterTurn("s"), Times.Once);
    }

    [Fact(DisplayName = "flush 抛异常时终态回调不得外抛")]
    public async Task OnExecutionCompleted_When_Flush_Throws_Should_Not_Throw()
    {
        var flush = new Mock<IMemoryFlushService>();
        flush.Setup(f => f.TryFlushAfterTurn(It.IsAny<string>()))
            .Throws(new InvalidOperationException("flush 内部故障"));
        var handler = BuildHandler(flush.Object);

        var act = () => handler.OnExecutionCompletedAsync(CompletedOutcome("s"));

        await act.Should().NotThrowAsync("观察者契约要求自身失败不得影响执行链");
    }

    [Fact(DisplayName = "DI 中观察者与具体处理器应解析为同一实例")]
    public void Di_Resolution_Should_Return_Same_Handler_Instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfigSectionStore>(new StubConfigSectionStore());
        services.AddSingleton(Mock.Of<IFileStore>());
        // flush 链路最终会实例化 LlmMemoryExtractor（抽取器），需要 LLM 补全端口占位
        services.AddSingleton(Mock.Of<Seeing.Agent.Abstractions.Llm.ITextCompletion>());
        services.AddMemoryServices(new StubConfigSectionRegistry(), "Data Source=:memory:");

        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<AgentTurnMemoryHandler>();
        var observer = provider.GetRequiredService<IExecutionOutcomeObserver>();

        observer.Should().BeSameAs(concrete,
            "观察者必须以工厂方式指向同一单例，否则内部状态会在两个实例间分裂");
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
        MemoryOptions options) =>
        new(flush, MemoryTestOptions.Monitor(options), NullLogger<AgentTurnMemoryHandler>.Instance);

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
