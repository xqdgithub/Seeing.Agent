using System.CommandLine;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "查看所有服务运行状态");

        command.SetAction(async parseResult =>
        {
            try
            {
                var workspaceRoot = ResolveWorkspaceRoot();
                var manager = new ServiceProcessManager(workspaceRoot);

                Console.WriteLine("服务状态:");
                Console.WriteLine(new string('-', 50));

                var anyRunning = false;
                foreach (var service in new[] { "webui", "gateway" })
                {
                    var running = manager.IsRunning(service);
                    if (running) anyRunning = true;
                    var icon = running ? "[运行中]" : "[已停止]";
                    Console.WriteLine($"  {service,-10} {icon}");
                }

                if (!anyRunning)
                {
                    Console.WriteLine();
                    Console.WriteLine("提示: 启动服务以获取运行详情");
                    return;
                }

                var gatewayPort = GetGatewayPort(workspaceRoot);
                using var apiClient = new ManagementApiClient($"http://127.0.0.1:{gatewayPort}");

                var status = await apiClient.GetStatusAsync();
                if (status != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("Gateway 详情:");
                    Console.WriteLine(new string('-', 50));
                    Console.WriteLine($"  端口:       {status.GatewayPort}");
                    Console.WriteLine($"  运行时间:   {status.Uptime}");
                    Console.WriteLine($"  活跃会话:   {status.ActiveSessions}");
                    Console.WriteLine($"  活跃执行:   {status.ActiveExecutions}");
                    Console.WriteLine($"  调度器:     {(status.SchedulerRunning ? "运行中" : "已停止")}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"状态查询失败: {ex.Message}");
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
