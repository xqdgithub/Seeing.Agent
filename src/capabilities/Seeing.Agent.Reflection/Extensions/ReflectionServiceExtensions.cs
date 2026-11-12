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

        // 手动触发的唤醒信号：触发器 Pulse、Worker 等待，必须同一实例。
        // 用具体类型登记并按具体类型取回，避免「接口与实现各一份」导致脉冲丢给无人等待的对象。
        services.TryAddSingleton<ReflectionWakeSignal>();

        // 「正在跑哪一个」的单槽：Worker 写、状态端口读，因此必须是同一实例。
        services.TryAddSingleton<ReflectionRunningTracker>();
        services.TryAddSingleton(sp =>
            new ReflectionIdleScanner(
                sp.GetRequiredService<IReflectionQueue>(),
                sp.GetRequiredService<Seeing.Session.Core.ISessionActivityTracker>(),
                sp.GetRequiredService<IOptionsMonitor<ReflectionOptions>>(),
                sp.GetService<Seeing.Session.Core.ISessionManager>(),
                sp.GetService<ILogger<ReflectionIdleScanner>>()));

        // 复盘产物根目录：跟随工作区的项目级 .seeing，缺失时退回临时目录（仅单元测试等无宿主场景）。
        // 用专用持有类型而非裸 string 注册：裸 string 会与容器里其它字符串服务冲突。
        // 注意：工作区未选定时 ProjectSeeingDirectory 是占位目录，这里必须先看 IsResolved。
        // 目录是**可变**的：首次选定与后续切换由 ReflectionWorkspaceParticipant 重新对齐。
        services.TryAddSingleton(sp =>
        {
            var workspace = sp.GetService<IWorkspaceProvider>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<ReflectionStorageRoot>();

            if (workspace is { IsResolved: true })
                return new ReflectionStorageRoot(
                    Path.Combine(workspace.ProjectSeeingDirectory, "reflection"), logger);

            logger?.LogWarning(
                "工作区尚未选定，复盘日志与用量账本改用临时目录：{Directory}",
                ReflectionStorageRoot.TempFallback);
            return new ReflectionStorageRoot(ReflectionStorageRoot.TempFallback, logger);
        });

        // journal / 记录页 / 账本都持有目录**持有者**而不是当时的字符串：
        // 否则切换工作区后它们继续读写旧工作区的产物（记录页看不到新记录、预算记在旧项目）。
        services.TryAddSingleton<IReflectionJournal>(sp =>
            new ReflectionJournal(sp.GetRequiredService<ReflectionStorageRoot>()));
        services.TryAddSingleton(sp =>
            new Seeing.Agent.Reflection.Ui.ReflectionRunReader(
                sp.GetRequiredService<ReflectionStorageRoot>()));
        // 记录页的写操作（删除记录 / 生成技能）：页面只依赖这一个入口。
        services.TryAddSingleton(sp =>
            new Seeing.Agent.Reflection.Ui.ReflectionRunOperations(
                sp.GetRequiredService<IReflectionJournal>(),
                sp.GetRequiredService<Seeing.Agent.Reflection.Ui.ReflectionRunReader>(),
                sp.GetRequiredService<ReflectionSuggestionsWriter>(),
                sp.GetService<ILogger<Seeing.Agent.Reflection.Ui.ReflectionRunOperations>>()));
        services.TryAddSingleton<IExecutionUsageLedger>(sp =>
            new ExecutionUsageLedger(sp.GetRequiredService<ReflectionStorageRoot>()));

        // 工作区切换参与者：把产物目录对齐到新工作区（会话存储的 SessionReloadHandler 的复盘版）。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWorkspaceApplyParticipant, ReflectionWorkspaceParticipant>());
        services.TryAddSingleton(sp =>
            new ReflectionSuggestionsWriter(
                sp.GetRequiredService<IOptionsMonitor<ReflectionOptions>>(),
                sp.GetRequiredService<ILogger<ReflectionSuggestionsWriter>>(),
                sp.GetService<Seeing.Agent.Abstractions.Skills.ISkillWriter>(),
                sp.GetService<Seeing.Agent.Abstractions.Skills.ISkillCatalog>()));

        // 手动触发入口：UI 只依赖端口，不触达内部服务。
        services.TryAddSingleton<IReflectionManualTrigger>(sp =>
            new ReflectionManualTrigger(
                sp.GetRequiredService<IReflectionQueue>(),
                sp.GetRequiredService<Seeing.Session.Core.ISessionManager>(),
                sp.GetRequiredService<ReflectionModuleActivity>(),
                sp.GetRequiredService<IOptionsMonitor<ReflectionOptions>>(),
                sp.GetService<ReflectionWakeSignal>()));

        // 只读运行态：设置卡与记录页据此显示「谁在排队 / 谁在跑」。
        services.TryAddSingleton<IReflectionStatus>(sp =>
            new ReflectionStatus(
                sp.GetRequiredService<ReflectionModuleActivity>(),
                sp.GetRequiredService<IExecutionUsageLedger>(),
                sp.GetRequiredService<IReflectionQueue>(),
                sp.GetRequiredService<ReflectionRunningTracker>(),
                sp.GetRequiredService<ReflectionStorageRoot>()));
        services.TryAddSingleton<IReflectionExecutor>(sp =>
            new ReflectionExecutor(
                sp.GetRequiredService<Seeing.Session.Core.ISessionManager>(),
                sp.GetRequiredService<Seeing.Session.Core.ISessionGroupManager>(),
                sp.GetRequiredService<Seeing.Agent.Abstractions.Runtime.ISessionRuntime>(),
                sp.GetRequiredService<IReflectionJournal>(),
                sp.GetRequiredService<IExecutionUsageLedger>(),
                sp.GetRequiredService<ReflectionRateLimiter>(),
                sp.GetRequiredService<ReflectionSuggestionsWriter>(),
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
