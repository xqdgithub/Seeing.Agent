using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Background;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Reflection.Integration;

namespace Seeing.Agent.Reflection.Extensions;

/// <summary>
/// 复盘能力 DI 注册扩展。
/// <para>全部使用 <c>TryAdd*</c>，重复调用幂等（不翻倍观察者、不重建单例）。</para>
/// </summary>
public static class ReflectionServiceExtensions
{
    /// <summary>
    /// 登记复盘能力。
    /// <para>
    /// 配置读取 <c>seeing.json</c> 的 <c>Reflection</c> 节（经 <see cref="IConfigSectionStore"/>），
    /// <b>不</b>读 Microsoft 的 <c>IConfiguration</c>——本仓能力包配置不进那条管道。
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="registry">配置节注册表；提供后配置节会出现在 WebUI 配置页并可被统一校验。</param>
    public static IServiceCollection AddSeeingReflection(
        this IServiceCollection services,
        IConfigSectionRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 登记配置节元信息：不做这一步，用户能在 seeing.json 里写配置但 WebUI 看不到该节，
        // 且配置校验不会覆盖它（与 Memory 的做法一致）。
        registry?.Register(ReflectionOptionsProvider.Meta);

        services.TryAddSingleton<ReflectionModuleActivity>();

        services.TryAddSingleton(sp =>
            new ReflectionOptionsProvider(
                sp.GetService<IConfigSectionStore>(),
                sp.GetService<ILogger<ReflectionOptionsProvider>>()));
        services.TryAddSingleton<IOptions<ReflectionOptions>>(sp =>
            new ReflectionOptionsAccessor(sp.GetRequiredService<ReflectionOptionsProvider>()));
        services.TryAddSingleton<IOptionsMonitor<ReflectionOptions>>(sp =>
            sp.GetRequiredService<ReflectionOptionsProvider>());

        services.TryAddSingleton<IReflectionQueue>(sp =>
            new ReflectionQueue(sp.GetRequiredService<IOptionsMonitor<ReflectionOptions>>()));
        services.TryAddSingleton<ReflectionTurnCounter>();
        services.TryAddSingleton<ReflectionRateLimiter>();

        // 复盘产物根目录：跟随工作区的项目级 .seeing，缺失时退回临时目录（仅单元测试等无宿主场景）。
        // 用专用持有类型而非裸 string 注册：裸 string 会与容器里其它字符串服务冲突。
        services.TryAddSingleton(sp =>
        {
            var workspace = sp.GetService<IWorkspaceProvider>();
            if (workspace is not null && !string.IsNullOrWhiteSpace(workspace.ProjectSeeingDirectory))
                return new ReflectionStorageRoot(Path.Combine(workspace.ProjectSeeingDirectory, "reflection"));

            var fallback = Path.Combine(Path.GetTempPath(), "seeing-reflection");
            sp.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(ReflectionServiceExtensions))
                .LogWarning("未找到工作区提供者，复盘日志与用量账本改用临时目录：{Directory}", fallback);
            return new ReflectionStorageRoot(fallback);
        });

        services.TryAddSingleton<IReflectionJournal>(sp =>
            new ReflectionJournal(sp.GetRequiredService<ReflectionStorageRoot>().Path));
        services.TryAddSingleton<IExecutionUsageLedger>(sp =>
            new ExecutionUsageLedger(sp.GetRequiredService<ReflectionStorageRoot>().Path));
        services.TryAddSingleton<IReflectionExecutor>(sp =>
            new ReflectionExecutor(
                sp.GetRequiredService<Seeing.Session.Core.ISessionManager>(),
                sp.GetRequiredService<Seeing.Session.Core.ISessionGroupManager>(),
                sp.GetRequiredService<Seeing.Agent.Abstractions.Runtime.ISessionRuntime>(),
                sp.GetRequiredService<IReflectionJournal>(),
                sp.GetRequiredService<IExecutionUsageLedger>(),
                sp.GetRequiredService<ReflectionRateLimiter>(),
                sp.GetRequiredService<IOptionsMonitor<ReflectionOptions>>(),
                sp.GetRequiredService<ILogger<ReflectionExecutor>>()));

        // 观察者：枚举槽用真实实现类型登记（TryAddEnumerable 按实现类型去重），
        // 具体类型从枚举槽取回，保证「接口 + 具体类型」解析到同一实例（轮次计数不分裂）。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IExecutionOutcomeObserver, ReflectionObserver>());
        services.TryAddSingleton(sp =>
            sp.GetServices<IExecutionOutcomeObserver>().OfType<ReflectionObserver>().First());

        // 用 AddModuleHostedService 让 IHostedService 与 IModuleHostedService 共用同一实例；
        // 外层判重保证重复调用不重复登记（TryAddEnumerable 不接受工厂描述符）。
        if (services.All(d => d.ServiceType != typeof(ReflectionWorker)))
            services.AddModuleHostedService<ReflectionWorker>();

        // 多模块共存：ISeeingModule 是多实现集合，必须按实现类型去重登记。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISeeingModule, ReflectionModule>());

        return services;
    }
}

/// <summary>
/// 复盘产物根目录持有者（日志与账本共用同一目录）。
/// <para>
/// 用专用类型而不是裸 <c>string</c> 登记：容器里可能存在其它字符串服务，裸 <c>string</c>
/// 会让「最后一个注册者获胜」意外改掉别人的依赖。
/// </para>
/// </summary>
internal sealed record ReflectionStorageRoot(string Path);
