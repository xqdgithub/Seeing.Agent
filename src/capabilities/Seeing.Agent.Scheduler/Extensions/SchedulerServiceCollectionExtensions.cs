using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Quartz;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Execution;
using Seeing.Agent.Scheduler.Hosting;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tools;

namespace Seeing.Agent.Scheduler.Extensions;

/// <summary>Scheduler DI 注册扩展</summary>
public static class SchedulerServiceCollectionExtensions
{
    /// <summary>
    /// 注册六个 cron 工具具体类型（由 SchedulerModule.Activate 挂到 IToolManager）。
    /// </summary>
    public static IServiceCollection AddSeeingSchedulerTools(this IServiceCollection services)
    {
        services.AddSingleton<CronListTool>();
        services.AddSingleton<CronCreateTool>();
        services.AddSingleton<CronDeleteTool>();
        services.AddSingleton<CronDisableTool>();
        services.AddSingleton<CronResumeTool>();
        services.AddSingleton<CronRunTool>();
        return services;
    }

    /// <summary>注册 Seeing.Agent.Scheduler 全部服务。须在 <c>AddSeeingCore</c> 之前调用。</summary>
    public static IServiceCollection AddSeeingScheduler(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        services.EnsureConfigSectionRegistry(registry);
        registry.Register(
            new ConfigSectionMeta("Scheduler", "scheduler.json", ConfigScope.ProjectOnly, typeof(SchedulerOptions)));

        // 配置提供者（需要在 Quartz 配置之前注册）
        services.AddSingleton<SchedulerOptionsProvider>();
        services.AddSingleton<ISchedulerOptionsProvider>(sp => sp.GetRequiredService<SchedulerOptionsProvider>());
        services.AddOptions<SchedulerOptions>();
        services.AddSingleton<SchedulerOptionsMonitorAdapter>();
        services.AddSingleton<IOptionsMonitor<SchedulerOptions>>(sp => sp.GetRequiredService<SchedulerOptionsMonitorAdapter>());
        services.AddSingleton<IOptions<SchedulerOptions>>(sp => sp.GetRequiredService<SchedulerOptionsMonitorAdapter>());

        // 配置重载处理器（依赖 SchedulerOptionsProvider，必须在其后注册）
        services.AddSingleton<IReloadHandler, SchedulerReloadHandler>();

        // 持久化
        services.AddSingleton<IScheduleRepository, JsonScheduleRepository>();

        // Quartz Jobs - 注册为 transient 以支持每次执行创建新实例
        services.AddTransient<AgentJob>();
        services.AddTransient<HeartbeatJob>();

        // Quartz 配置 - 使用 AddQuartz 进行 DI 集成
        // 注意：AddQuartz 会自动使用 Microsoft DI JobFactory
        services.AddQuartz(q =>
        {
            q.SchedulerId = "SeeingAgentScheduler";
            q.SchedulerName = "Seeing.Agent Scheduler";
            
            // 使用默认线程池
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 10);
            
            // 默认使用内存存储
            q.UseInMemoryStore();
        });

        // 调度引擎（管理 Quartz 生命周期）
        services.AddSingleton<QuartzSchedulerEngine>();

        // 调度管理器
        services.AddSingleton<ScheduleManager>();
        services.AddSingleton<IScheduleManager>(sp => sp.GetRequiredService<ScheduleManager>());
        services.AddSingleton<IJobExecutionListener>(sp => sp.GetRequiredService<ScheduleManager>());
        services.AddSingleton<IScheduleStatusQuery>(sp =>
            new ScheduleStatusQueryAdapter(sp.GetRequiredService<IScheduleManager>()));

        // 投递器
        services.AddSingleton<LogScheduleDispatcher>();
        services.AddSingleton<SessionScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => new CompositeScheduleDispatcher(new IScheduledJobDispatcher[]
        {
            sp.GetRequiredService<SessionScheduleDispatcher>(),
            sp.GetRequiredService<LogScheduleDispatcher>()
        }));

        services.AddSeeingSchedulerTools();

        services.AddSingleton<SchedulerModuleActivity>();
        services.AddSingleton<ISeeingModule>(sp => new SchedulerModule(
            sp.GetRequiredService<SchedulerModuleActivity>(),
            sp.GetRequiredService<IScheduleManager>(),
            sp.GetService<Seeing.Agent.Abstractions.Ui.IUiContributionRegistry>()));

        // Hosted Service（管理调度器生命周期）
        services.AddModuleHostedService<ScheduleHostedService>();

        return services;
    }

    /// <summary>注册 Seeing.Agent.Scheduler 全部服务（带配置）。须在 <c>AddSeeingCore</c> 之前调用。</summary>
    public static IServiceCollection AddSeeingScheduler(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        Action<SchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configure);
        services.EnsureConfigSectionRegistry(registry);
        registry.Register(
            new ConfigSectionMeta("Scheduler", "scheduler.json", ConfigScope.ProjectOnly, typeof(SchedulerOptions)));

        // 先应用配置
        var options = new SchedulerOptions();
        configure(options);

        // 注册配置实例
        services.AddSingleton(options);
        services.AddSingleton<SchedulerOptionsProvider>(sp =>
        {
            var configStore = sp.GetRequiredService<IConfigSectionStore>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SchedulerOptionsProvider>>();
            var provider = new SchedulerOptionsProvider(configStore, logger);
            provider.Reload();
            return provider;
        });
        services.AddSingleton<ISchedulerOptionsProvider>(sp => sp.GetRequiredService<SchedulerOptionsProvider>());

        // 配置重载处理器（依赖 SchedulerOptionsProvider，必须在其后注册）
        services.AddSingleton<IReloadHandler, SchedulerReloadHandler>();

        // 持久化
        services.AddSingleton<IScheduleRepository, JsonScheduleRepository>();

        // Quartz Jobs - 注册为 transient 以支持每次执行创建新实例
        services.AddTransient<AgentJob>();
        services.AddTransient<HeartbeatJob>();

        // Quartz 配置 - 使用 AddQuartz 进行 DI 集成
        // 使用委托工厂方法，在运行时获取配置
        services.AddQuartz((q, sp) =>
        {
            var optionsProvider = sp.GetRequiredService<ISchedulerOptionsProvider>();
            var schedulerOptions = optionsProvider.Current;
            var workspaceProvider = sp.GetRequiredService<IWorkspaceProvider>();
            var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("QuartzSqliteInitializer");
            
            q.SchedulerId = "SeeingAgentScheduler";
            q.SchedulerName = "Seeing.Agent Scheduler";
            
            // 使用默认线程池
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = schedulerOptions.MaxConcurrentJobs);
            
            // 根据配置选择存储方式
            if (schedulerOptions.Persistence.Enabled && 
                schedulerOptions.Persistence.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
            {
                var dbPath = schedulerOptions.Persistence.ConnectionString;
                if (string.IsNullOrEmpty(dbPath))
                {
                    dbPath = $"Data Source={workspaceProvider.ProjectSeeingDirectory}/quartz.db";
                }

                // 自动初始化 SQLite 数据库表结构
                Task.Run(() => QuartzSqliteInitializer.InitializeAsync(dbPath, logger)).GetAwaiter().GetResult();

                q.UsePersistentStore(store =>
                {
                    store.UseGenericDatabase("SQLite-Microsoft", db => db.ConnectionString = dbPath);
                    store.UseSystemTextJsonSerializer();
                    store.UseProperties = true;
                    store.PerformSchemaValidation = false;
                });
            }
            else
            {
                q.UseInMemoryStore();
            }
        });

        // 调度引擎（管理 Quartz 生命周期）
        services.AddSingleton<QuartzSchedulerEngine>();

        // 调度管理器
        services.AddSingleton<ScheduleManager>();
        services.AddSingleton<IScheduleManager>(sp => sp.GetRequiredService<ScheduleManager>());
        services.AddSingleton<IJobExecutionListener>(sp => sp.GetRequiredService<ScheduleManager>());
        services.AddSingleton<IScheduleStatusQuery>(sp =>
            new ScheduleStatusQueryAdapter(sp.GetRequiredService<IScheduleManager>()));

        // 投递器
        services.AddSingleton<LogScheduleDispatcher>();
        services.AddSingleton<SessionScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => new CompositeScheduleDispatcher(new IScheduledJobDispatcher[]
        {
            sp.GetRequiredService<SessionScheduleDispatcher>(),
            sp.GetRequiredService<LogScheduleDispatcher>()
        }));

        services.AddSeeingSchedulerTools();

        services.AddSingleton<SchedulerModuleActivity>();
        services.AddSingleton<ISeeingModule>(sp => new SchedulerModule(
            sp.GetRequiredService<SchedulerModuleActivity>(),
            sp.GetRequiredService<IScheduleManager>(),
            sp.GetService<Seeing.Agent.Abstractions.Ui.IUiContributionRegistry>()));

        // Hosted Service（管理调度器生命周期）
        services.AddModuleHostedService<ScheduleHostedService>();

        return services;
    }
}