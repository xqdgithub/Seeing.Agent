using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Extensions;
using Seeing.Agent.Reflection.Integration;
using Seeing.Session;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Integration;

/// <summary>
/// DI 级守门：从真实容器解析观察者，验证「枚举槽」与「具体类型」是同一实例、
/// 且门禁经接口调用真正生效。
/// <para>
/// 这条链最容易出错的形态是：<c>ShouldObserve</c> 只在具体类上实现（不是接口默认实现），
/// 若容器把枚举槽与具体类型解析成两个实例，观察者内部的轮次计数就会分裂；
/// 若调用方走的是接口默认实现，门禁则完全不生效。单测覆盖不到，必须走 DI。
/// </para>
/// </summary>
public sealed class ReflectionDependencyInjectionTests
{
    [Fact]
    public void Observer_Should_Resolve_As_Same_Instance_From_Both_Slots()
    {
        using var provider = BuildProvider();

        var fromEnumerable = provider.GetServices<IExecutionOutcomeObserver>()
            .OfType<ReflectionObserver>()
            .Single();
        var fromConcrete = provider.GetRequiredService<ReflectionObserver>();

        fromEnumerable.Should().BeSameAs(fromConcrete,
            "枚举槽与具体类型必须是同一实例，否则轮次计数会分裂");
    }

    [Fact]
    public void Observer_Should_Gate_Via_Interface_Invocation()
    {
        // 通过接口调用（不是具体类型）——这正是 ExecutionRunner 的调用方式。
        using var provider = BuildProvider();

        var observer = provider.GetServices<IExecutionOutcomeObserver>()
            .OfType<ReflectionObserver>()
            .Single();

        observer.ShouldObserve(CompletedOutcome(SessionOrigin.User, "build"))
            .Should().BeFalse("默认关闭时不得触发复盘");
    }

    [Fact]
    public void Observer_Should_Reject_Reflection_Origin_Via_Interface()
    {
        using var provider = BuildProvider(options => options.Enabled = true);

        var observer = provider.GetServices<IExecutionOutcomeObserver>()
            .OfType<ReflectionObserver>()
            .Single();

        observer.ShouldObserve(CompletedOutcome(SessionOrigin.User, "build"))
            .Should().BeTrue("开启后普通顶层会话应当被观察");

        observer.ShouldObserve(CompletedOutcome(SessionOrigin.Reflection, "build"))
            .Should().BeFalse("Origin=Reflection 是防递归第 2 重，必须挡住");
    }

    [Fact]
    public void AddSeeingReflection_Should_Be_Idempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSessionManager();
        AddRuntimeStubs(services);

        services.AddSeeingReflection();
        services.AddSeeingReflection();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IExecutionOutcomeObserver>()
            .OfType<ReflectionObserver>()
            .Should().HaveCount(1, "重复登记不得产生两个观察者（会双倍计数）");
        provider.GetServices<ISeeingModule>()
            .Count(m => m.Id == "reflection")
            .Should().Be(1);
    }

    [Fact]
    public void Reflection_Module_Activity_Should_Be_Inactive_Until_Activated()
    {
        using var provider = BuildProvider();

        // 默认必须是 false：能力集收窄时模块不会被 Activate，
        // 若默认 true 则 worker 会跑但 agent 从未注册 → 孤儿会话累积。
        provider.GetRequiredService<ReflectionModuleActivity>().IsActive
            .Should().BeFalse("模块未激活前后台不得工作");
    }

    private static ServiceProvider BuildProvider(Action<ReflectionOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSessionManager();
        AddRuntimeStubs(services);
        services.AddSeeingReflection();

        if (configure is not null)
        {
            // 覆盖配置源：用固定快照替代真实配置节存储。
            services.AddSingleton<IOptionsMonitor<ReflectionOptions>>(_ =>
            {
                var options = new ReflectionOptions();
                configure(options);
                return new SnapshotMonitor(options);
            });
        }

        return services.BuildServiceProvider();
    }

    /// <summary>补上观察者/工作者构造所需的最小运行期桩（本测试不执行真实复盘）。</summary>
    private static void AddRuntimeStubs(IServiceCollection services)
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime.Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InboxResult.Ok());
        services.AddSingleton(runtime.Object);

        var groups = new Mock<ISessionGroupManager>();
        groups.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        services.AddSingleton(groups.Object);
    }

    private static ExecutionOutcome CompletedOutcome(SessionOrigin origin, string agentName)
        => new()
        {
            SessionId = "session-1",
            ExecutionId = "exec-1",
            Status = ExecutionStatus.Completed,
            AgentName = agentName,
            AgentExecuted = true,
            IsTopLevel = true,
            Origin = origin
        };

    /// <summary>固定快照的 monitor：本测试只关心门禁，不需要热重载。</summary>
    private sealed class SnapshotMonitor(ReflectionOptions value) : IOptionsMonitor<ReflectionOptions>
    {
        public ReflectionOptions CurrentValue { get; } = value;

        public ReflectionOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ReflectionOptions, string?> listener) =>
            new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
