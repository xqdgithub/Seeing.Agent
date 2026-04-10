using Terminal.Gui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Extensions;
using Seeing.Agent.MCP;
using Seeing.Agent.NewTui.Infrastructure;
using Seeing.Agent.NewTui.State;
using Seeing.Agent.NewTui.Services;
using Seeing.Agent.NewTui.Views;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;

namespace Seeing.Agent.NewTui;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // 帮助参数
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

        // 配置 DI
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddSeeingAgent(configuration);
        services.PostConfigure<SeeingAgentOptions>(ApplyApiKeysFromEnvironment);

        // TUI 服务
        services.AddSingleton<AppState>(sp => new AppState { WorkspaceRoot = workspace });
        services.AddSingleton<TuiPermissionChannel>();
        services.AddSingleton<AgentRunner>();
        services.AddSingleton<HomeView>();
        services.AddSingleton<App>();

        using var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Seeing.NewTui");

        // 启动 HostedService
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        // 初始化工作区
        try
        {
            await InitializeWorkspaceAsync(provider, workspace, logger);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"初始化失败: {ex.Message}");
            return 1;
        }

        // 运行 TUI
        try
        {
            Application.UseSystemConsole = true;
            Application.Init();

            SynchronizationContext.SetSynchronizationContext(
                new TerminalGuiSynchronizationContext());

            var app = provider.GetRequiredService<App>();
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"运行错误: {ex.Message}");
            return 1;
        }
        finally
        {
            Application.Shutdown();
            try
            {
                await provider.GetRequiredService<McpClientManager>().DisconnectAllAsync();
            }
            catch { }
        }

        return 0;
    }

    private static async Task InitializeWorkspaceAsync(IServiceProvider provider, string workspace, ILogger logger)
    {
        var state = provider.GetRequiredService<AppState>();
        var registry = provider.GetRequiredService<IAgentRegistry>();

        // 设置默认 Agent
        try
        {
            state.CurrentAgent = await registry.GetDefaultAgentNameAsync();
        }
        catch (InvalidOperationException)
        {
            var primaries = await registry.GetPrimaryAgentsAsync();
            if (primaries.Count == 0)
                throw new InvalidOperationException(
                    "注册中心中没有任何可用的 Agent。请在 ~/.seeing/seeing.json 中配置 SeeingAgent:Agents，或确保扩展已加载。");
            state.CurrentAgent = primaries[0].Name;
        }

        // 加载技能
        await SkillPathBootstrap.ApplyAsync(
            provider.GetRequiredService<SkillManager>(),
            provider.GetRequiredService<IOptions<SeeingAgentOptions>>(),
            workspace);

        // 连接 MCP
        await ConnectMcpAsync(provider, state, logger);
    }

    private static async Task ConnectMcpAsync(IServiceProvider provider, AppState state, ILogger logger)
    {
        var mcp = provider.GetRequiredService<McpClientManager>();
        var invoker = provider.GetRequiredService<ToolInvoker>();

        var configs = SeeingMcpConfigLoader.LoadDefault(state.WorkspaceRoot, logger);

        foreach (var cfg in configs)
        {
            try
            {
                await mcp.ConnectAsync(cfg);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP 连接失败: {Name}", cfg.Name);
            }
        }

        foreach (var tool in mcp.GetToolsAsITools())
            invoker.RegisterTool(tool);
    }

    private static void ApplyApiKeysFromEnvironment(SeeingAgentOptions options)
    {
        foreach (var p in options.Providers.Values)
        {
            if (!string.IsNullOrWhiteSpace(p.ApiKey)) continue;

            if (p.Type == ProviderType.OpenAI)
                p.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            else if (p.Type == ProviderType.Anthropic)
                p.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Seeing.Agent NewTUI - AI Agent 终端界面

        用法:
          seeing-newtui [工作区路径] [选项]

        选项:
          --no-walk-up   不向上一级查找工作区根
          -h, --help     显示此帮助

        配置文件:
          ~/.seeing/seeing.json  用户级配置
          ~/.seeing/mcp.json     MCP服务器配置
          ~/.seeing/skills/      用户技能目录
        """);
    }
}

internal class TerminalGuiSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        Application.MainLoop.Invoke(() => d(state));
    }
}