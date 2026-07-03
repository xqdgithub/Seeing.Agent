using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Commands;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Commands;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Execution;
using Seeing.Agent.Scheduler.Heartbeat;
using Seeing.Agent.Scheduler.Hosting;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Persistence;

namespace Seeing.Agent.Scheduler.Extensions;

/// <summary>Scheduler DI 注册扩展</summary>
public static class SchedulerServiceCollectionExtensions
{
    /// <summary>注册 Seeing.Agent.Scheduler 全部服务</summary>
    public static IServiceCollection AddSeeingScheduler(this IServiceCollection services)
    {
        services.AddSingleton<SchedulerOptionsProvider>();
        services.AddSingleton<IScheduleRepository, JsonScheduleRepository>();
        services.AddSingleton<InProcessSchedulerEngine>();
        services.AddSingleton<ScheduledAgentRunner>();
        services.AddSingleton<IScheduledJobExecutor, AgentScheduledJobExecutor>();
        services.AddSingleton<IHeartbeatRunner, HeartbeatRunner>();
        services.AddSingleton<IScheduleManager, ScheduleManager>();

        services.AddSingleton<LogScheduleDispatcher>();
        services.AddSingleton<SessionScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => new CompositeScheduleDispatcher(new IScheduledJobDispatcher[]
        {
            sp.GetRequiredService<SessionScheduleDispatcher>(),
            sp.GetRequiredService<LogScheduleDispatcher>()
        }));

        services.AddHostedService<ScheduleHostedService>();
        services.AddHostedService<SchedulerCommandRegistrationHostedService>();

        return services;
    }
}
