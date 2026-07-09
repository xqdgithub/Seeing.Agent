using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Commands;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Execution;
using Seeing.Agent.Scheduler.Hosting;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;

namespace Seeing.Agent.Scheduler.Extensions;

/// <summary>Scheduler DI 注册扩展</summary>
public static class SchedulerServiceCollectionExtensions
{
    /// <summary>注册 Seeing.Agent.Scheduler 全部服务</summary>
    public static IServiceCollection AddSeeingScheduler(this IServiceCollection services)
    {
        // 配置提供者（需要在 Quartz 配置之前注册）
        services.AddSingleton<SchedulerOptionsProvider>();
        services.AddSingleton<ISchedulerOptionsProvider>(sp => sp.GetRequiredService<SchedulerOptionsProvider>());

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

        // 投递器
        services.AddSingleton<LogScheduleDispatcher>();
        services.AddSingleton<SessionScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => new CompositeScheduleDispatcher(new IScheduledJobDispatcher[]
        {
            sp.GetRequiredService<SessionScheduleDispatcher>(),
            sp.GetRequiredService<LogScheduleDispatcher>()
        }));

        // Hosted Service（管理调度器生命周期）
        services.AddHostedService<ScheduleHostedService>();
        services.AddHostedService<SchedulerCommandRegistrationHostedService>();

        return services;
    }

    /// <summary>注册 Seeing.Agent.Scheduler 全部服务（带配置）</summary>
    public static IServiceCollection AddSeeingScheduler(
        this IServiceCollection services,
        Action<SchedulerOptions> configure)
    {
        // 先应用配置
        var options = new SchedulerOptions();
        configure(options);

        // 注册配置实例
        services.AddSingleton(options);
        services.AddSingleton<SchedulerOptionsProvider>(sp =>
        {
            var configManager = sp.GetRequiredService<Seeing.Agent.Configuration.UnifiedConfigManager>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SchedulerOptionsProvider>>();
            var provider = new SchedulerOptionsProvider(configManager, logger);
            provider.Reload();
            return provider;
        });
        services.AddSingleton<ISchedulerOptionsProvider>(sp => sp.GetRequiredService<SchedulerOptionsProvider>());

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
            var workspaceProvider = sp.GetRequiredService<Seeing.Agent.Configuration.IWorkspaceProvider>();
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
                QuartzSqliteInitializer.InitializeAsync(dbPath, logger).GetAwaiter().GetResult();

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

        // 投递器
        services.AddSingleton<LogScheduleDispatcher>();
        services.AddSingleton<SessionScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => new CompositeScheduleDispatcher(new IScheduledJobDispatcher[]
        {
            sp.GetRequiredService<SessionScheduleDispatcher>(),
            sp.GetRequiredService<LogScheduleDispatcher>()
        }));

        // Hosted Service（管理调度器生命周期）
        services.AddHostedService<ScheduleHostedService>();
        services.AddHostedService<SchedulerCommandRegistrationHostedService>();

        return services;
    }
}