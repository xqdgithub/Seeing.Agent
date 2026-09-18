using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Hosting.Commands;
using Seeing.Agent.Hosting.Commands.BuiltIn;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Hosting.Tools;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Commands.Discovery;
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Abstractions.Agents;

namespace Seeing.Agent.Hosting;

/// <summary>
/// Hosting 层 DI 注册扩展（ChatOrchestrator / ExecutionEngine / Task tools / 命令发现）
/// </summary>
public static class HostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ChatOrchestrator 及其依赖
    /// </summary>
    public static IServiceCollection AddChatOrchestrator(this IServiceCollection services)
    {
        // 注册单例服务（跨会话共享）
        services.AddSingleton<ChatExecutionQueue>();
        services.AddSingleton<ChatRunTracker>();

        // 注册命令发现（具体类 + Abstractions 端口）
        services.AddSingleton<CommandDiscovery>();
        services.AddSingleton<ICommandDiscovery>(sp => sp.GetRequiredService<CommandDiscovery>());

        // 注册命令提供者（Skill 命令由 Skills 模块经 ICommand 贡献）
        services.AddSingleton<BuiltInCommands>();
        services.AddSingleton<SessionCommands>();
        services.AddSingleton<AgentCommands>();
        services.AddSingleton<ToolsCommands>();

        // 注册 ChatOrchestrator (Singleton：权限审批已改经 IPermissionRequestManager/事件流，依赖均为 Singleton)
        services.AddSingleton<IChatOrchestrator, ChatOrchestrator>();

        // 任务与 Todo 工具具体类型（由 HostingModule.Activate → IToolManager 挂载）
        services.AddSingleton(sp => new TaskTool(
            sp.GetRequiredService<ILogger<TaskTool>>(),
            sp.GetRequiredService<ISessionManager>(),
            sp.GetRequiredService<ISessionGroupManager>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<IAgentLoopScheduler>(),
            sp.GetRequiredService<IExecutionSubmitter>(),
            sp.GetRequiredService<IExecutionStatusProvider>(),
            sp.GetRequiredService<IExecutionEventPublisher>()));
        services.AddSingleton(sp => new TaskStatusTool(
            sp.GetRequiredService<ILogger<TaskStatusTool>>(),
            sp.GetRequiredService<ISessionManager>(),
            sp.GetRequiredService<ISessionGroupManager>(),
            sp.GetRequiredService<IExecutionSubmitter>(),
            sp.GetRequiredService<IExecutionStatusProvider>()));
        services.AddSingleton<TodoWriteTool>();
        services.AddSingleton<ISeeingModule, HostingModule>();

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

        // 压缩进度事件适配器：LlmSummarizer 经 ICompactionEventSink 发布进度 → 执行事件流（WebUI 实时展示）
        services.AddSingleton<ICompactionEventSink, Events.CompactionEventSink>();

        // 统一压缩入口：Started → 压缩 → Completed/Failed 事件序列（自动门控 / /compact 命令共用）
        services.AddSingleton<CompactionRunner>();

        // 注册执行任务服务（Singleton，后台执行）
        services.AddSingleton<ExecutionJobService>();
        services.AddSingleton<IExecutionSubmitter>(sp => sp.GetRequiredService<ExecutionJobService>());
        services.AddSingleton<IExecutionStatusProvider>(sp => sp.GetRequiredService<ExecutionJobService>());
        services.AddSingleton<IExecutionInFlightBoundary>(sp => sp.GetRequiredService<ExecutionJobService>());

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
        var discovery = services.GetRequiredService<ICommandDiscovery>();

        // 发现所有命令提供者（通过 DI 获取实例）
        var commandProviders = new object?[]
        {
            services.GetService<BuiltInCommands>(),
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

        // 模块贡献的 ICommand（Skills 等经 DI 登记的静态命令；动态 skill 由 SkillLoader 写入）
        foreach (var command in services.GetServices<ICommand>())
            registry.Register(command);

        return services;
    }
}
