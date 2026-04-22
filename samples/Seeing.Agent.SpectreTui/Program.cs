using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Agent.SpectreTui.Infrastructure;

namespace Seeing.Agent.SpectreTui;

/// <summary>
/// Spectre.Console TUI 应用入口
/// </summary>
internal static class Program
{
    /// <summary>
    /// 主入口点
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        // 设置控制台 UTF-8 编码
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // 处理帮助参数
        if (args is ["--help"] or ["-h"])
        {
            PrintHelp();
            return 0;
        }

        // 解析工作区
        var pathArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        var workspaceRoot = ResolveWorkspace(pathArg);

        Directory.SetCurrentDirectory(workspaceRoot);

        // 构建配置
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // 配置 DI
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        // 日志配置
        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Warning);
        });

        // 注册 Seeing.Agent 核心服务（可选，后续集成时启用）
        // services.AddSeeingAgent(configuration);

        // 注册 Spectre TUI 服务
        services.AddSpectreTui(configuration, workspaceRoot);

        using var provider = services.BuildServiceProvider();

        // 配置默认命令
        provider.ConfigureDefaultCommands();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("SpectreTui");

        // 启动 HostedServices（如果有）
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        // 运行 TUI
        try
        {
            AnsiConsole.MarkupLine("[green]启动 Spectre.Console TUI...[/]");
            AnsiConsole.WriteLine();

            var mainApp = provider.GetRequiredService<MainApp>();
            await mainApp.RunAsync();

            AnsiConsole.MarkupLine("[green]应用已退出[/]");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Application error");
            AnsiConsole.MarkupLine($"[red]运行错误: {EscapeMarkup(ex.Message)}[/]");
            return 1;
        }
    }

    /// <summary>
    /// 解析工作区路径
    /// </summary>
    private static string ResolveWorkspace(string? pathArg)
    {
        if (!string.IsNullOrEmpty(pathArg))
        {
            var path = Path.GetFullPath(pathArg);
            if (Directory.Exists(path))
                return path;
        }

        // 默认使用当前目录
        var currentDir = Directory.GetCurrentDirectory();
        
        // 向上查找工作区标记
        var walkUp = true;
        while (walkUp)
        {
            if (File.Exists(Path.Combine(currentDir, ".git")) ||
                File.Exists(Path.Combine(currentDir, ".gitignore")) ||
                Directory.Exists(Path.Combine(currentDir, ".git")) ||
                File.Exists(Path.Combine(currentDir, "Seeing.Agent.slnx")))
            {
                return currentDir;
            }

            var parent = Directory.GetParent(currentDir);
            if (parent == null)
                break;

            currentDir = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// 打印帮助信息
    /// </summary>
    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("""
        [bold cyan]Seeing.Agent Spectre TUI[/] - AI Agent 终端界面

        [yellow]用法[/]:
          seeing-spectre-tui [工作区路径] [选项]

        [yellow]选项[/]:
          -h, --help     显示此帮助

        [yellow]快捷键[/]:
          Ctrl+P         打开命令面板
          Enter          发送消息
          Ctrl+Enter     发送消息（多行模式）
          Ctrl+C         取消当前任务
          Ctrl+M         切换多行模式
          Esc            关闭面板

        [yellow]命令[/]:
          /help          显示帮助
          /exit          退出应用
          /clear         清空消息
          /status        显示状态

        """);
    }

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}