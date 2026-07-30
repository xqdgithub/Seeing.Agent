using System.CommandLine;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StartCommand
{
    public static Command Create()
    {
        var serviceArg = new Argument<string>("service", "要启动的服务: webui 或 gateway");

        var command = new Command("start", "启动指定的服务")
        {
            serviceArg
        };

        command.SetHandler(async (service) =>
        {
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

                manager.CleanupDeadPidFile(service);

                if (manager.IsRunning(service))
                {
                    Console.WriteLine($"服务 {service} 已在运行中");
                    return;
                }

                var cliDir = AppDomain.CurrentDomain.BaseDirectory;
                string dllName = service == "webui"
                    ? "Seeing.Agent.WebUI.dll"
                    : "Seeing.Gateway.Server.dll";

                var dllPath = FindDll(cliDir, dllName);
                var extraArgs = service == "webui"
                    ? new[] { "--urls", "http://0.0.0.0:5000" }
                    : Array.Empty<string>();

                Console.WriteLine($"正在启动 {service}...");
                var process = await manager.StartAsync(service, dllPath, extraArgs);

                var gatewayPort = GetGatewayPort(workspaceRoot);
                var apiClient = new ManagementApiClient($"http://127.0.0.1:{gatewayPort}");

                Console.Write($"等待 {service} 就绪");
                await manager.WaitForReadyAsync(apiClient);
                Console.WriteLine(" 就绪!");

                if (service == "webui")
                    Console.WriteLine($"WebUI 已启动: http://0.0.0.0:5000");
                else
                    Console.WriteLine($"Gateway 已启动: http://127.0.0.1:{gatewayPort}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"启动失败: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, serviceArg);

        return command;
    }

    private static string ResolveWorkspaceRoot()
    {
        var env = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return env;

        return Directory.GetCurrentDirectory();
    }

    private static string FindDll(string cliDir, string dllName)
    {
        var sameDir = Path.Combine(cliDir, dllName);
        if (File.Exists(sameDir)) return sameDir;

        var parentDir = Path.Combine(cliDir, "..", dllName);
        if (File.Exists(parentDir)) return Path.GetFullPath(parentDir);

        var repoRoot = Path.GetFullPath(Path.Combine(cliDir, "..", "..", "..", ".."));
        var webuiBuild = Path.Combine(repoRoot, "samples",
            dllName.Replace(".dll", ""), "bin", "Debug", "net10.0", dllName);
        if (File.Exists(webuiBuild)) return webuiBuild;

        throw new FileNotFoundException(
            $"找不到 {dllName}。请先执行 dotnet build，或设置 SEEING_WORKSPACE_ROOT 环境变量");
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
                {
                    return port.GetInt32();
                }
            }
        }
        catch { }

        return 8765;
    }
}
