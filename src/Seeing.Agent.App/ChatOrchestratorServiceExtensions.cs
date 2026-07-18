using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App.Commands;
using Seeing.Agent.App.Commands.BuiltIn;
using Seeing.Agent.App.Execution;
using Seeing.Agent.App.Internal;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Discovery;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Skills;

namespace Seeing.Agent.App;

/// <summary>
/// ChatOrchestrator DI 注册扩展
/// </summary>
public static class ChatOrchestratorServiceExtensions
{
    /// <summary>
    /// 注册 ChatOrchestrator 及其依赖
    /// </summary>
    public static IServiceCollection AddChatOrchestrator(this IServiceCollection services)
    {
        // 注册单例服务（跨会话共享）
        services.AddSingleton<ChatExecutionQueue>();
        services.AddSingleton<ChatRunTracker>();

        // 注册命令发现
        services.AddSingleton<CommandDiscovery>();

        // 注册命令提供者
        services.AddSingleton<BuiltInCommands>();
        services.AddSingleton<SkillCommands>();
        services.AddSingleton<SessionCommands>();
        services.AddSingleton<AgentCommands>();
        services.AddSingleton<ToolsCommands>();

        // 注册 ChatOrchestrator (Scoped because it depends on IPermissionChannel which is Scoped)
        services.AddScoped<IChatOrchestrator, ChatOrchestrator>();

        return services;
    }

    /// <summary>
    /// 注册执行引擎（后台执行服务）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configure">可选配置</param>
    public static IServiceCollection AddExecutionEngine(this IServiceCollection services, Action<ExecutionOptions>? configure = null)
    {
        // 注册配置选项
        var options = new ExecutionOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // 注册事件发布器
        services.AddSingleton<IExecutionEventPublisher, ExecutionEventPublisher>();
        services.AddSingleton<ISessionEventBus, ExecutionSessionEventBus>();

        // 注册执行任务服务（Singleton，后台执行）
        services.AddSingleton<ExecutionJobService>();

        // idle resume + Session 事件总线接线
        services.AddHostedService<AgentLoopSchedulerHostedService>();

        return services;
    }

    /// <summary>
    /// 初始化命令发现（在服务提供者构建后调用）
    /// </summary>
    public static IServiceProvider InitializeCommands(this IServiceProvider services)
    {
        var registry = services.GetRequiredService<ICommandRegistry>();
        var discovery = services.GetRequiredService<CommandDiscovery>();
        
        // 发现所有命令提供者（通过 DI 获取实例）
        var commandProviders = new object?[]
        {
            services.GetService<BuiltInCommands>(),
            services.GetService<SkillCommands>(),
            services.GetService<SessionCommands>(),
            services.GetService<AgentCommands>(),
            services.GetService<ToolsCommands>()
        };

        foreach (var provider in commandProviders)
        {
            if (provider != null)
            {
                var commands = discovery.DiscoverFromType(provider.GetType(), provider);
                registry.RegisterAll(commands);
            }
        }

        // 动态注册所有 skill 命令（Native 和 ACP 两个版本）
        var skillManager = services.GetService<SkillManager>();
        if (skillManager != null)
        {
            foreach (var skillInfo in skillManager.GetAllSkillInfos().Values)
            {
                // Native 版本 - 扩展为详情
                var nativeCommand = new DynamicSkillCommand(skillManager, skillInfo);
                registry.Register(nativeCommand);

                // ACP 版本 - 透传给 ACP 后端
                var acpCommand = new AcpDynamicSkillCommand(skillInfo.Name, skillInfo.Description);
                registry.Register(acpCommand);
            }
        }

        return services;
    }
}
