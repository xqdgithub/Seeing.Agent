using System.CommandLine;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StopCommand
{
    public static Command Create()
    {
        var serviceArg = new Argument<string>("service") { Description = "要停止的服务: webui 或 gateway" };

        var command = new Command("stop", "停止指定的服务")
        {
            serviceArg
        };

        command.SetAction(async parseResult =>
        {
            var service = parseResult.GetValue<string>(serviceArg);
            service = service.ToLowerInvariant();
            if (service != "webui" && service != "gateway")
            {
                Console.Error.WriteLine("错误: service 必须是 'webui' 或 'gateway'");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var workspaceRoot = ResolveWorkspaceRoot();
                var manager = new ServiceProcessManager(workspaceRoot);

                if (!manager.IsRunning(service))
                {
                    Console.WriteLine($"服务 {service} 未在运行");
                    manager.CleanupDeadPidFile(service);
                    return;
                }

                var gatewayPort = GetGatewayPort(workspaceRoot);
                using var apiClient = new ManagementApiClient($"http://127.0.0.1:{gatewayPort}");

                Console.WriteLine($"正在停止 {service}...");
                var ok = await manager.StopAsync(service, apiClient, CancellationToken.None);

                if (ok)
                    Console.WriteLine($"{service} 已停止");
                else
                    Console.WriteLine($"{service} 已强制终止");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"停止失败: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    private static string ResolveWorkspaceRoot()
    {
        var env = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        return Directory.GetCurrentDirectory();
    }

    private static int GetGatewayPort(string workspaceRoot)
    {
        try
        {
            var seeingJson = Path.Combine(workspaceRoot, ".seeing", "seeing.json");
            if (File.Exists(seeingJson))
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seeingJson));
                if (json.RootElement.TryGetProperty("SeeingAgent", out var sa) &&
                    sa.TryGetProperty("Gateway", out var gw) &&
                    gw.TryGetProperty("Port", out var port))
                    return port.GetInt32();
            }
        }
        catch { }
        return 8765;
    }
}
