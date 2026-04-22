using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.SpectreTui.Core.State;
using Seeing.Agent.SpectreTui.Services;
using Seeing.Agent.SpectreTui.UI;

namespace Seeing.Agent.SpectreTui.Infrastructure;

/// <summary>
/// DI 服务注册扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Spectre TUI 所有服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置对象</param>
    /// <param name="workspaceRoot">工作区根目录</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddSpectreTui(
        this IServiceCollection services,
        IConfiguration configuration,
        string workspaceRoot)
    {
        // ========== 核心状态 ==========

        // AgentContext（运行时上下文）
        services.AddSingleton(sp => new AgentContext
        {
            WorkspaceRoot = workspaceRoot,
            CurrentAgentKey = "primary",
            CurrentModel = configuration["SeeingAgent:DefaultModel"],
            ToolCount = 0,
            SkillCount = 0,
            McpServerCount = 0,
            ExtensionCount = 0,
            MessageCount = 0
        });

        // InputState（输入状态管理）
        services.AddSingleton<InputState>();

        // ========== UI 组件 ==========

        // StatusBar（状态栏）
        services.AddSingleton<StatusBar>();

        // InputBox（输入框）
        services.AddSingleton<InputBox>();

        // CommandPalette（命令面板）
        services.AddSingleton<CommandPalette>();

        // ========== 核心服务 ==========

        // LayoutService（布局服务）
        services.AddSingleton<LayoutService>();

        // InputService（键盘输入服务）
        services.AddSingleton<InputService>();

        // ========== 主应用 ==========

        // MainApp（应用入口）
        services.AddSingleton<MainApp>();

        return services;
    }

    /// <summary>
    /// 配置默认命令
    /// </summary>
    /// <param name="provider">服务提供者</param>
    public static void ConfigureDefaultCommands(this IServiceProvider provider)
    {
        var commandPalette = provider.GetRequiredService<CommandPalette>();
        var inputState = provider.GetRequiredService<InputState>();
        var mainApp = provider.GetRequiredService<MainApp>();

        // 注册默认命令
        commandPalette.RegisterCommands(new[]
        {
            new CommandItem
            {
                Id = "toggle_multiline",
                Name = "切换多行模式",
                Description = "切换输入框为多行/单行模式",
                Group = "输入",
                Execute = () => inputState.ToggleMultilineMode()
            },
            new CommandItem
            {
                Id = "clear_history",
                Name = "清空历史记录",
                Description = "清除所有输入历史",
                Group = "输入",
                Execute = () => inputState.ClearHistory()
            },
            new CommandItem
            {
                Id = "clear_messages",
                Name = "清空消息",
                Description = "清除所有聊天消息",
                Group = "显示",
                Execute = () => provider.GetRequiredService<LayoutService>().ClearMessages()
            },
            new CommandItem
            {
                Id = "show_status",
                Name = "显示状态",
                Description = "显示当前 Agent 状态信息",
                Group = "信息",
                Execute = () =>
                {
                    var ctx = provider.GetRequiredService<AgentContext>();
                    var layout = provider.GetRequiredService<LayoutService>();
                    layout.AddSystemMessage($"Agent: {ctx.CurrentAgentKey}");
                    layout.AddSystemMessage($"Model: {ctx.CurrentModel ?? "default"}");
                    layout.AddSystemMessage($"Messages: {layout.MessageCount}");
                }
            },
            new CommandItem
            {
                Id = "help",
                Name = "帮助",
                Description = "显示快捷键和用法说明",
                Group = "信息",
                Execute = () =>
                {
                    var layout = provider.GetRequiredService<LayoutService>();
                    layout.AddSystemMessage("快捷键: Ctrl+P 打开命令面板, Enter 发送");
                    layout.AddSystemMessage("命令: /help, /exit, /clear");
                }
            },
            new CommandItem
            {
                Id = "quit",
                Name = "退出",
                Description = "退出应用程序",
                Group = "系统",
                Execute = () => mainApp.RequestStop()
            }
        });
    }
}