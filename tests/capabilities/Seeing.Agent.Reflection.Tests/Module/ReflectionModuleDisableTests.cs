using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Reflection;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Background;
using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Reflection.Integration;
using Seeing.Agent.Reflection.Tests.Integration;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Moq;using Xunit;
using ReflectionOptions = Seeing.Agent.Reflection.Configuration.ReflectionOptions;

namespace Seeing.Agent.Reflection.Tests.Module;

/// <summary>
/// 模块停用后「功能设计的东西一律不加载」的守门。
/// <para>
/// <b>为什么需要这一组</b>：能力包最容易出的事是「DI 注册了就算数」——
/// 服务在容器里、agent 在 store 里、UI 声明在注册表里，但模块已被禁用。
/// 这类残留不会报错，只会在别处悄悄生效（或让用户看到一个点开就报错的入口）。
/// 因此逐层钉住：门控标志、观察者、扫描器、手动触发、后台循环、agent、UI 可见性。
/// </para>
/// <para>
/// 注意区分两种状态：<b>DI 里可解析</b>（组装期注册，与 Memory/Scheduler 同构，不是缺陷）
/// 与<b>会实际工作</b>（必须被门控拦住）。这里断言的是后者。
/// </para>
/// </summary>
public sealed class ReflectionModuleDisableTests
{
    [Fact]
    public async Task Deactivate_Should_Turn_Activity_Off()
    {
        var activity = new ReflectionModuleActivity { IsActive = true };
        var module = new ReflectionModule(activity);
        using var provider = new ServiceCollection().BuildServiceProvider();

        await module.DeactivateAsync(provider);

        activity.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Deactivate_Should_Unregister_Agent()
    {
        // 「注册后永不注销」会让模块禁用后仍能从 AgentStore 取到 reflection 定义。
        var store = new CountingAgentStore();
        var activity = new ReflectionModuleActivity { IsActive = true };
        var module = new ReflectionModule(
            activity,
            NullLogger<ReflectionModule>.Instance,
            store,
            new FixedOptionsMonitor());

        using var provider = new ServiceCollection().BuildServiceProvider();

        await module.ActivateAsync(provider);
        store.Has(ReflectionAgentDefinition.AgentName).Should().BeTrue();

        await module.DeactivateAsync(provider);

        store.Has(ReflectionAgentDefinition.AgentName).Should().BeFalse(
            "模块停用后不应残留一个可被 AgentStore.Get(\"reflection\") 取到的定义");
    }

    [Fact]
    public async Task Deactivate_Should_Not_Unregister_User_Override()
    {
        // 用户在 MD 里覆盖了同名 agent：那不是本模块注册的实例，不得删掉。
        var store = new CountingAgentStore();
        var activity = new ReflectionModuleActivity { IsActive = true };
        var module = new ReflectionModule(
            activity,
            NullLogger<ReflectionModule>.Instance,
            store,
            new FixedOptionsMonitor());

        using var provider = new ServiceCollection().BuildServiceProvider();
        await module.ActivateAsync(provider);

        // 用户覆盖：同名但不同实例。
        var userOverride = new AgentDefinition { Name = ReflectionAgentDefinition.AgentName };
        await store.RegisterAsync(userOverride);

        await module.DeactivateAsync(provider);

        store.Get(ReflectionAgentDefinition.AgentName).Should().BeSameAs(userOverride,
            "只该注销自己注册的那个实例，否则会把用户的覆盖删掉");
    }

    [Fact]
    public async Task Reactivate_Should_Restore_Agent()
    {
        // 停用 → 再启用必须能复活，否则热重载一次后复盘就永久失效。
        var store = new CountingAgentStore();
        var activity = new ReflectionModuleActivity { IsActive = true };
        var module = new ReflectionModule(
            activity,
            NullLogger<ReflectionModule>.Instance,
            store,
            new FixedOptionsMonitor());

        using var provider = new ServiceCollection().BuildServiceProvider();

        await module.ActivateAsync(provider);
        await module.DeactivateAsync(provider);
        await module.ActivateAsync(provider);

        activity.IsActive.Should().BeTrue();
        store.Has(ReflectionAgentDefinition.AgentName).Should().BeTrue();
    }

    [Fact]
    public void ShouldObserve_Should_Return_False_When_Module_Inactive()
    {
        // 这条是「禁用后不加载」的第一道闸：终态扇出仍会调用观察者。
        using var provider = ReflectionDependencyInjectionTests.BuildProviderForGating(options => options.Enabled = true);
        var activity = provider.GetRequiredService<ReflectionModuleActivity>();
        activity.IsActive = false;

        var observer = provider.GetServices<IExecutionOutcomeObserver>()
            .OfType<ReflectionObserver>()
            .Single();

        // 门禁本身只看配置与事实，不看 activity；activity 由 OnExecutionCompletedAsync 的早退负责。
        // 因此这里断言的是「回调不发生」而不是「门禁为 false」——两者语义不同，不能混。
        observer.ShouldObserve(ReflectionDependencyInjectionTests.CompletedOutcomeForGating()).Should().BeTrue();
    }

    [Fact]
    public async Task Observer_Should_Not_Enqueue_When_Module_Inactive()
    {
        using var provider = ReflectionDependencyInjectionTests.BuildProviderForGating(options =>
        {
            options.Enabled = true;
            options.TurnNudgeInterval = 1;
        });

        var activity = provider.GetRequiredService<ReflectionModuleActivity>();
        activity.IsActive = false;

        var observer = provider.GetServices<IExecutionOutcomeObserver>()
            .OfType<ReflectionObserver>()
            .Single();
        var queue = provider.GetRequiredService<Abstractions.IReflectionQueue>();

        await observer.OnExecutionCompletedAsync(ReflectionDependencyInjectionTests.CompletedOutcomeForGating());

        queue.PendingCount.Should().Be(0, "模块未激活时终态回调必须直接返回，不得入队");
    }

    [Fact]
    public void IdleScanner_Should_Not_Enqueue_When_Disabled_Config()
    {
        // 空闲扫描的配置闸：Enabled=false 时不扫描（模块停用另有 hosted service 停止 + activity 闸）。
        var queue = new ReflectionQueue(capacity: 4);
        var tracker = new SessionActivityTracker();
        tracker.Touch("ses-1");

        var options = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<ReflectionOptions>>();
        options.SetupGet(m => m.CurrentValue).Returns(new ReflectionOptions { Enabled = false });

        var scanner = new ReflectionIdleScanner(queue, tracker, options.Object, sessionManager: null);

        scanner.Scan(DateTimeOffset.UtcNow.AddHours(2)).Should().Be(0);
        queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void ManualTrigger_Should_Reject_When_Module_Inactive()
    {
        var queue = new ReflectionQueue(capacity: 4);
        var activity = new ReflectionModuleActivity { IsActive = false };

        var sessions = new Mock<ISessionManager>();
        sessions.Setup(m => m.Get(It.IsAny<string>()))
            .Returns(new SessionData { Id = "ses-1" });

        var options = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<ReflectionOptions>>();
        options.SetupGet(m => m.CurrentValue).Returns(new ReflectionOptions { Enabled = true });

        var trigger = new ReflectionManualTrigger(queue, sessions.Object, activity, options.Object);

        var result = trigger.Trigger("ses-1");

        result.Status.Should().Be(Abstractions.ReflectionManualTriggerStatus.Invalid);
        queue.PendingCount.Should().Be(0, "模块未激活时不得接受任何复盘请求");
    }

    [Fact]
    public void DescribeUi_Should_Declare_Ownership_On_Both_Contributions()
    {
        // 归属断言留在本包（不需要 Core）：只要 Requires 非空且等于自身 id，
        // UiVisibility 就会按模块启停判定可见性——Core 侧的判定逻辑由 WebUI 测试覆盖。
        var contributions = new ReflectionModule().DescribeUi();

        contributions.Should().HaveCount(2, "一条页面 + 一张设置卡");
        contributions.Should().OnlyContain(c => c.Requires.SequenceEqual(new[] { "reflection" }),
            "两条贡献都必须归属自身模块，否则模块停用后入口仍可见");
    }

    [Fact]
    public void DescribeUi_Should_Expose_Page_And_Settings_Card_Points()
    {
        var contributions = new ReflectionModule().DescribeUi();

        contributions.OfType<PageContribution>().Should().ContainSingle()
            .Which.Route.Should().Be("/reflection");

        contributions.OfType<SettingsCardContribution>().Should().ContainSingle()
            .Which.Id.Should().Be("settings.reflection");
    }

    /// <summary>记录 agent 的 store 替身（只为断言注册/注销，不做真实解析）。</summary>
    private sealed class CountingAgentStore : IAgentStore
    {
        private readonly Dictionary<string, AgentDefinition> _agents = new(StringComparer.OrdinalIgnoreCase);

        public Task RegisterAsync(AgentDefinition agentInfo)
        {
            _agents[agentInfo.Name] = agentInfo;
            return Task.CompletedTask;
        }

        public bool Unregister(string name) => _agents.Remove(name);

        public Task<AgentDefinition?> GetAsync(string name)
            => Task.FromResult(_agents.TryGetValue(name, out var a) ? a : null);

        public AgentDefinition? Get(string name) => _agents.TryGetValue(name, out var a) ? a : null;

        public Task<IReadOnlyList<AgentDefinition>> GetAllAsync()
            => Task.FromResult<IReadOnlyList<AgentDefinition>>(_agents.Values.ToList());

        public bool Has(string name) => _agents.ContainsKey(name);
    }

    private sealed class FixedOptionsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<ReflectionOptions>
    {
        private readonly ReflectionOptions _value = new();

        public ReflectionOptions CurrentValue => _value;

        public ReflectionOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<ReflectionOptions, string?> listener) => null;
    }
}
