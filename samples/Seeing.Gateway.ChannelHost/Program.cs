using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Gateway;
using Seeing.Gateway.Plugins;

namespace Seeing.Gateway.ChannelHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var arguments = ChannelHostArguments.Parse(args);
        if (arguments.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(arguments.PluginPath) || string.IsNullOrWhiteSpace(arguments.ConfigPath))
        {
            Console.Error.WriteLine("缺少 --plugin 或 --config 参数。");
            PrintHelp();
            return 1;
        }

        var plugin = LoadPlugin(arguments.PluginPath);
        if (plugin is null)
        {
            Console.Error.WriteLine($"无法从程序集加载 IGatewayChannelPlugin: {arguments.PluginPath}");
            return 1;
        }

        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(builder =>
            {
                builder.Sources.Clear();
                builder.AddJsonFile(arguments.ConfigPath, optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                var workspaceRoot = ChannelHostWorkspace.InferFromConfigPath(arguments.ConfigPath)
                    ?? Directory.GetCurrentDirectory();
                services.AddSingleton<IWorkspaceProvider>(_ => new WorkspaceProvider(workspaceRoot));
                plugin.ConfigureServices(services, context.Configuration);
                services.AddHostedService<ChannelBridgeHostedService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        Console.WriteLine($"Seeing Gateway ChannelHost [{plugin.DisplayName}]");
        Console.WriteLine($"ChannelId: {plugin.ChannelId}");
        Console.WriteLine($"Config: {Path.GetFullPath(arguments.ConfigPath)}");
        Console.WriteLine("按 Ctrl+C 退出。");

        await host.RunAsync();
        return 0;
    }

    private static IGatewayChannelPlugin? LoadPlugin(string pluginPath)
    {
        var fullPath = Path.GetFullPath(pluginPath);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(IGatewayChannelPlugin).IsAssignableFrom(t)
                                 && !t.IsInterface
                                 && !t.IsAbstract);
        return pluginType is null ? null : (IGatewayChannelPlugin)Activator.CreateInstance(pluginType)!;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Seeing.Gateway.ChannelHost

            用法:
              dotnet Seeing.Gateway.ChannelHost.dll --plugin <plugin.dll> --config <config.json>

            参数:
              --plugin   Channel 插件程序集路径
              --config   Channel 运行时配置文件路径
              --help     显示帮助
            """);
    }
}

internal sealed class ChannelHostArguments
{
    public string? PluginPath { get; init; }
    public string? ConfigPath { get; init; }
    public bool ShowHelp { get; init; }

    public static ChannelHostArguments Parse(string[] args)
    {
        string? plugin = null;
        string? config = null;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plugin" when i + 1 < args.Length:
                    plugin = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    config = args[++i];
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
            }
        }

        return new ChannelHostArguments
        {
            PluginPath = plugin,
            ConfigPath = config,
            ShowHelp = showHelp
        };
    }
}

internal static class ChannelHostWorkspace
{
    /// <summary>
    /// 从 <c>{workspace}/.seeing/gateway-clients/{channel}.json</c> 推断工作区根目录。
    /// </summary>
    internal static string? InferFromConfigPath(string configPath)
    {
        var clientsDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (string.IsNullOrEmpty(clientsDir))
            return null;

        var seeingDir = Path.GetDirectoryName(clientsDir);
        if (string.IsNullOrEmpty(seeingDir)
            || !".seeing".Equals(Path.GetFileName(seeingDir), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(seeingDir);
    }
}

internal sealed class ChannelBridgeHostedService : IHostedService
{
    private readonly IChannelBridge _bridge;
    private readonly ILogger<ChannelBridgeHostedService> _logger;

    public ChannelBridgeHostedService(IChannelBridge bridge, ILogger<ChannelBridgeHostedService> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("启动 Channel Bridge: {ChannelId}", _bridge.ChannelId);
        await _bridge.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("停止 Channel Bridge: {ChannelId}", _bridge.ChannelId);
        return _bridge.StopAsync(cancellationToken);
    }
}
