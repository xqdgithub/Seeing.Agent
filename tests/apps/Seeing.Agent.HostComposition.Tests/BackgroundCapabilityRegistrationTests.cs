using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Hosting;

namespace Seeing.Agent.HostComposition.Tests;

/// <summary>
/// 后台活动体系的组合根守门：能力集登记、DI 单例同一性、启动收敛与投递循环的宿主挂载。
/// <para>
/// 这些断言都在<b>真实容器</b>上做：后台活动涉及「同一实例的两个视图」（manager/control、
/// outbox 公开读侧与 internal 写侧），手工 new 一个管理器看不出来，只有从容器解析才能暴露
/// 「各注册了一份」这类缺陷。
/// </para>
/// </summary>
public class BackgroundCapabilityRegistrationTests
{
    /// <summary>能力集与组合根都必须登记 <c>background</c> 模块，否则工具与 bash background 全是死代码。</summary>
    [Fact]
    public async Task FullCapabilities_ShouldIncludeBackgroundModule()
    {
        BuiltInCapabilitySets.Full.Should().Contain("background");

        await using var provider = BuildProvider();

        provider.GetServices<ISeeingModule>()
            .Should().Contain(m => m.Id == "background",
                "Full 能力集与组合根必须同时登记，缺失时 BuiltInScenariosTests 也会红");
    }

    /// <summary>会话面与系统面必须解析到同一个 manager：状态权威只有一处。</summary>
    [Fact]
    public async Task Manager_And_Control_ShouldResolveToSameInstance()
    {
        await using var provider = BuildProvider();

        var manager = provider.GetRequiredService<IBackgroundActivityManager>();
        var control = provider.GetRequiredService<IBackgroundActivityControl>();

        control.Should().BeSameAs(manager,
            "取消与查询必须看到同一份状态；拆成两个实例会让取消对查询不可见");
    }

    /// <summary>
    /// outbox 的公开读侧（泵取/待投递数）与 internal 写侧（入队/切换丢弃）必须是同一实例：
    /// 否则切换时标 dropped 的批次与投递循环读到的不是同一份内存状态。
    /// </summary>
    [Fact]
    public async Task Outbox_ShouldResolveToSameInstance_ForQueueAndInterface()
    {
        await using var provider = BuildProvider();

        var outbox = provider.GetRequiredService<IBackgroundActivityOutbox>();

        // 写侧端口是 internal（不属于公开契约），从实现所在程序集按名取类型，
        // 避免为了测试把 Core 内部接口暴露成公开 API。
        var queueType = outbox.GetType().Assembly
            .GetType("Seeing.Agent.Core.Background.IBackgroundActivityNotificationQueue", throwOnError: true)!;

        var queue = provider.GetService(queueType);

        queue.Should().NotBeNull("写侧端口必须与公开读侧共享同一单例");
        queue.Should().BeSameAs(outbox);
    }

    /// <summary>启动收敛服务必须由宿主挂载：孤儿活动与死宿主 pending 只在启动时有机会被收拾。</summary>
    [Fact]
    public void RecoveryService_ShouldBeRegisteredAsHostedService()
    {
        var services = BuildServices();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService)
            && IsImplementationOf(d, "BackgroundActivityRecoveryService"));
    }

    /// <summary>投递循环必须由宿主挂载，否则完成通知永远停在 outbox 里。</summary>
    [Fact]
    public void NotificationDispatcher_ShouldBeRegisteredAsHostedService()
    {
        var services = BuildServices();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService)
            && IsImplementationOf(d, "BackgroundActivityNotificationDispatcher"));
    }

    /// <summary>保留期回收必须由宿主挂载，否则活动根与 outbox 只会单调增长。</summary>
    [Fact]
    public void RetentionService_ShouldBeRegisteredAsHostedService()
    {
        var services = BuildServices();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService)
            && IsImplementationOf(d, "BackgroundActivityRetentionService"));
    }

    /// <summary>工作区切换参与者必须登记：改根前取消旧根活动、改根后收敛新根孤儿。</summary>
    [Fact]
    public void WorkspaceParticipant_ShouldBeRegistered()
    {
        var services = BuildServices();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IWorkspaceApplyParticipant)
            && IsImplementationOf(d, "BackgroundActivityWorkspaceParticipant"));
    }

    /// <summary>描述符可能是 <c>AddHostedService&lt;T&gt;</c>（带实现类型）或工厂注册，两种都认。</summary>
    private static bool IsImplementationOf(ServiceDescriptor descriptor, string typeName)
    {
        if (descriptor.ImplementationType?.Name == typeName)
            return true;

        var factoryType = descriptor.ImplementationFactory?.Method.ReturnType;
        return factoryType is not null && factoryType.Name == typeName;
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<IConfiguration>(configuration);

        services.AddSeeingFullCapabilities(registry, configuration, includeGatewayServer: false);

        // 宿主通过各自 Host Shape 提供 subagent（HostingModule）；后台活动通知需要会话运行时与目录。
        services.AddSessionHosting();
        services.AddExecutionEngine();

        // 真实的 manager / outbox / hosted 服务都注入 ILogger<T>。
        services.AddLogging();

        services.AddSeeingCore(registry);

        return services;
    }

    private static ServiceProvider BuildProvider() => BuildServices().BuildServiceProvider();
}
