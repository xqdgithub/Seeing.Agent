using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Gateway.Plugins;
using System.Diagnostics;
using System.Runtime.Loader;

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
                // 宿主被强杀/崩溃时兜底自灭：父进程（Gateway 宿主）退出后本进程优雅退出，
                // 防止 Windows 无进程树级联导致的孤儿进程
                if (arguments.ParentPid is int parentPid)
                    services.AddHostedService(sp => new ParentExitWatchdogService(
                        parentPid,
                        sp.GetRequiredService<IHostApplicationLifetime>(),
                        sp.GetRequiredService<ILogger<ParentExitWatchdogService>>()));
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
    public int? ParentPid { get; init; }
    public bool ShowHelp { get; init; }

    public static ChannelHostArguments Parse(string[] args)
    {
        string? plugin = null;
        string? config = null;
        int? parentPid = null;
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
                case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid):
                    parentPid = pid;
                    i++;
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
            ParentPid = parentPid,
            ShowHelp = showHelp
        };
    }
}

/// <summary>
/// 父进程退出看门狗：父进程（Gateway 宿主）死亡后优雅停止本 Host。
/// 手动运行（未传 <c>--parent-pid</c>）时不启用，不影响调试。
/// </summary>
internal sealed class ParentExitWatchdogService : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly int _parentPid;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ParentExitWatchdogService> _logger;
    private CancellationTokenSource? _cts;

    public ParentExitWatchdogService(
        int parentPid,
        IHostApplicationLifetime lifetime,
        ILogger<ParentExitWatchdogService> logger)
    {
        _parentPid = parentPid;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = WatchAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                if (IsProcessAlive(_parentPid))
                    continue;

                _logger.LogWarning("父进程 {ParentPid} 已退出，ChannelHost 自行停止", _parentPid);
                _lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
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

        // Host 统一负责 Gateway 连接（连接即注册 channel）
        var connection = _bridge.GatewayConnection;
        if (connection != null)
        {
            await connection.ConnectAsync(_bridge.ChannelId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Gateway 连接探测已启动，连接成功后将注册 Channel: {ChannelId}", _bridge.ChannelId);
        }

        await _bridge.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("停止 Channel Bridge: {ChannelId}", _bridge.ChannelId);
        await _bridge.StopAsync(cancellationToken);

        var connection = _bridge.GatewayConnection;
        if (connection != null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Gateway 连接已释放: {ChannelId}", _bridge.ChannelId);
        }
    }
}
