using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Gateway;
using Seeing.Gateway.Client;
using Seeing.Gateway.WeCom;
using Seeing.Gateway.WeCom.Extensions;

namespace Seeing.Gateway.WeCom.Demo;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((_, config) =>
            {
                var wecomPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".seeing",
                    "wecom.json");
                if (File.Exists(wecomPath))
                    config.AddJsonFile(wecomPath, optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<WeComOptions>(context.Configuration.GetSection(WeComOptions.SectionName));
                services.Configure<GatewayClientOptions>(context.Configuration.GetSection(GatewayClientOptions.SectionName));
                services.PostConfigure<GatewayClientOptions>(options => options.Transport = GatewayClientTransport.WebSocket);
                services.AddSeeingWeComChannel();
                services.AddHostedService<WeComBridgeHostedService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        global::System.Console.WriteLine("Seeing Gateway WeCom Demo");
        global::System.Console.WriteLine("按 Ctrl+C 退出。");

        await host.RunAsync();
        return 0;
    }
}

internal sealed class WeComBridgeHostedService : IHostedService
{
    private readonly IChannelBridge _bridge;
    private readonly ILogger<WeComBridgeHostedService> _logger;

    public WeComBridgeHostedService(IChannelBridge bridge, ILogger<WeComBridgeHostedService> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("启动 WeCom Channel Bridge...");
        await _bridge.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("停止 WeCom Channel Bridge...");
        return _bridge.StopAsync(cancellationToken);
    }
}
