using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Seeing.Agent.Commands;
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
        // 配置提供者
        services.AddSingleton<SchedulerOptionsProvider>();
        services.AddSingleton<ISchedulerOptionsProvider>(sp => sp.GetRequiredService<SchedulerOptionsProvider>());

        // 持久化
        services.AddSingleton<IScheduleRepository, JsonScheduleRepository>();

        // Quartz Jobs - 注册为 transient 以支持每次执行创建新实例
        services.AddTransient<AgentJob>();
        services.AddTransient<HeartbeatJob>();

        // Quartz 配置（AddQuartz 会自动配置 Microsoft DI JobFactory）
        services.AddQuartz(q =>
        {
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 3);
            q.UseInMemoryStore();
            q.SchedulerId = "SeeingAgentScheduler";
            q.SchedulerName = "Seeing.Agent Scheduler";
            // Quartz.Extensions.Hosting 自动使用 Microsoft DI JobFactory
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

        // 其余注册
        return services.AddSeeingScheduler();
    }
}