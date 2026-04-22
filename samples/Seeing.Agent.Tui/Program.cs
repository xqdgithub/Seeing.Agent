
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Extensions;
using Seeing.Agent.MCP;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Commands;
using Seeing.Agent.Tui.Infrastructure;
using Seeing.Agent.Tui.Integration.Adapters;
using Seeing.Agent.Tui.Services;
using Seeing.Agent.Tui.UI;

namespace Seeing.Agent.Tui;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 设置控制台UTF-8编码
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
        var walkUp = !args.Contains("--no-walk-up", StringComparer.Ordinal);

        string workspace;
        try
        {
            workspace = WorkspaceResolver.Resolve(pathArg, walkUp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"错误: {ex.Message}");
            return 1;
        }

        Directory.SetCurrentDirectory(workspace);

        // 初始化用户配置目录
        try
        {
            if (SeeingUserProfileInitializer.EnsureCreated())
                Console.WriteLine($"已初始化用户目录: {SeeingLayout.UserSeeingDirectory}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"无法创建用户目录: {ex.Message}");
            return 1;
        }

        // 构建配置
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(SeeingLayout.UserSeeingJsonPath, optional: true)
            .AddEnvironmentVariables()
            .Build();

        // 配置DI
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddSeeingAgent(configuration);
        services.PostConfigure<SeeingAgentOptions>(TuiBootstrap.ApplyApiKeysFromEnvironment);

        // 注册TUI服务
        services.AddSingleton<TuiState>(sp => new TuiState { WorkspaceRoot = workspace });
        services.AddSingleton<IPermissionChannel, ConsolePermissionChannel>();
        services.AddSingleton<IAgentAdapter, DefaultAgentAdapter>();
        services.AddSingleton<ChatOrchestrator>();
        
        // 注册事件通道和路由服务
        services.AddSingleton<EventChannelService>();
        services.AddSingleton<RenderService>();
        services.AddSingleton<EventRouter>();
        
        services.AddTuiCommands();
        services.AddSingleton<TuiApp>();

        using var provider = services.BuildServiceProvider();

        // 初始化命令注册
        provider.InitializeCommands();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.Tui");

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        // 初始化工作区
        try
        {
            await TuiBootstrap.InitializeWorkspaceAsync(provider, logger);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"初始化失败: {ex.Message}");
            return 1;
        }

        // 运行TUI
        try
        {
            var tuiApp = provider.GetRequiredService<TuiApp>();
            tuiApp.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"运行错误: {ex.Message}");
            return 1;
        }
        finally
        {
            try
            {
                await provider.GetRequiredService<McpClientManager>().DisconnectAllAsync();
            }
            catch
            {
                // ignore
            }
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Seeing.Agent TUI - AI Agent 终端界面

        用法:
          seeing-tui [工作区路径] [选项]

        选项:
          --no-walk-up   不向上一级查找工作区根
          -h, --help     显示此帮助

        配置文件:
          ~/.seeing/seeing.json  用户级配置
          ~/.seeing/mcp.json     MCP服务器配置
          ~/.seeing/skills/      用户技能目录
          ~/.seeing/rules/       用户规则目录

        项目配置:
          .seeing/mcp.json       项目MCP配置
          skills/                项目技能目录
          rules/                 项目规则目录
        """);
    }
}