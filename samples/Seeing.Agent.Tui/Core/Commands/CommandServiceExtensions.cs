using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Commands;
using Seeing.Agent.Extensions;
using Seeing.Agent.Tui.Core.Commands.Impl;

namespace Seeing.Agent.Tui.Core.Commands;

/// <summary>
/// 命令系统 DI 扩展 - 使用核心库接口
/// </summary>
public static class CommandServiceExtensions
{
    /// <summary>
    /// 注册 TUI 命令（使用核心库 ICommand 接口）
    /// </summary>
    public static IServiceCollection AddTuiCommands(this IServiceCollection services)
    {
        // 使用核心库的命令系统
        services.AddCommandSystem(options =>
        {
            // 可以添加自动发现配置
        });

        // 注册需要 TuiState 的命令
        services.AddSingleton<ClearCommand>();
        services.AddSingleton<CancelCommand>();
        services.AddSingleton<MultilineCommand>();
        services.AddSingleton<SearchCommand>();
        services.AddSingleton<FoldCommand>();
        services.AddSingleton<RulesCommand>();

        // 注册需要服务的命令
        services.AddSingleton<HelpCommand>();
        services.AddSingleton<AgentCommand>();
        services.AddSingleton<ModelCommand>();
        services.AddSingleton<ToolsCommand>();
        services.AddSingleton<SkillsCommand>();
        services.AddSingleton<McpCommand>();

        return services;
    }

    /// <summary>
    /// 初始化 TUI 命令（在服务提供者构建后调用）
    /// </summary>
    public static IServiceProvider InitializeTuiCommands(this IServiceProvider services)
    {
        var registry = services.GetRequiredService<ICommandRegistry>();

        // 注册所有 TUI 命令
        registry.Register(services.GetRequiredService<HelpCommand>());
        registry.Register(services.GetRequiredService<ClearCommand>());
        registry.Register(services.GetRequiredService<CancelCommand>());
        registry.Register(services.GetRequiredService<MultilineCommand>());
        registry.Register(services.GetRequiredService<SearchCommand>());
        registry.Register(services.GetRequiredService<FoldCommand>());
        registry.Register(services.GetRequiredService<RulesCommand>());
        registry.Register(services.GetRequiredService<AgentCommand>());
        registry.Register(services.GetRequiredService<ModelCommand>());
        registry.Register(services.GetRequiredService<ToolsCommand>());
        registry.Register(services.GetRequiredService<SkillsCommand>());
        registry.Register(services.GetRequiredService<McpCommand>());

        return services;
    }
}