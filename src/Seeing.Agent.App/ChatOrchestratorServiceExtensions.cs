using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App.Commands;
using Seeing.Agent.App.Commands.BuiltIn;
using Seeing.Agent.App.Internal;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Discovery;

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

        // 注册命令分发器
        services.AddSingleton<CommandDispatcher>(sp =>
        {
            var registry = sp.GetRequiredService<ICommandRegistry>();
            var commandService = sp.GetService<ICommandService>();
            var logger = sp.GetService<ILogger<CommandDispatcher>>();
            return new CommandDispatcher(registry, commandService, logger);
        });

        // 注册命令发现
        services.AddSingleton<CommandDiscovery>();

        // 注册内置命令
        services.AddSingleton<BuiltInCommands>();
        services.AddSingleton<SkillCommands>();

        // 注册 ChatOrchestrator (Scoped because it depends on IPermissionChannel which is Scoped)
        services.AddScoped<IChatOrchestrator, ChatOrchestrator>();

        return services;
    }

    /// <summary>
    /// 初始化命令发现（在服务提供者构建后调用）
    /// </summary>
    public static IServiceProvider InitializeCommands(this IServiceProvider services)
    {
        var registry = services.GetRequiredService<ICommandRegistry>();
        var discovery = services.GetRequiredService<CommandDiscovery>();
        
        // 发现 BuiltInCommands 中的命令
        var builtIn = services.GetService<BuiltInCommands>();
        if (builtIn != null)
        {
            var commands = discovery.DiscoverFromType(builtIn);
            registry.RegisterAll(commands);
        }

        // 发现 SkillCommands 中的命令
        var skillCommands = services.GetService<SkillCommands>();
        if (skillCommands != null)
        {
            var commands = discovery.DiscoverFromType(skillCommands);
            registry.RegisterAll(commands);
        }

        // 发现其他程序集中的命令
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.StartsWith("Seeing.Agent") == true || 
                        a.FullName?.StartsWith("Seeing.Session") == true);

        foreach (var assembly in assemblies)
        {
            try
            {
                var commands = discovery.DiscoverFromAssembly(assembly, services);
                registry.RegisterAll(commands);
            }
            catch
            {
                // 忽略加载错误
            }
        }

        return services;
    }
}
