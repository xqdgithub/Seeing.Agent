using System.CommandLine;
using System.Linq;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StartCommand
{
    public static Command Create()
    {
        var serviceArg = new Argument<string>("service") { Description = "要启动的服务: webui 或 gateway" };

        var command = new Command("start", "启动指定的服务")
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
                manager.Registry.PruneDead();

                var running = manager.Registry.Load()
                    .Where(i => i.Service == service && i.WorkspaceRoot == workspaceRoot)
                    .ToList();
                if (running.Count > 0)
                {
                    Console.WriteLine($"服务 {service} 已在 {workspaceRoot} 运行中（端口: {running[0].Port}）");
                    return;
                }

                var cliDir = AppDomain.CurrentDomain.BaseDirectory;
                string dllName = service == "webui"
                    ? "Seeing.Agent.WebUI.dll"
                    : "Seeing.Gateway.Server.dll";
                var dllPath = FindDll(cliDir, dllName);

                // webui 动态分配端口；gateway 使用工作区配置端口（其自身据此监听）
                int port;
                string[]? extraArgs;
                if (service == "webui")
                {
                    port = new PortAllocator().NextAvailable(5000);
                    extraArgs = new[] { "--urls", $"http://127.0.0.1:{port}" };
                }
                else
                {
                    port = GetGatewayPort(workspaceRoot);
                    extraArgs = Array.Empty<string>();
                }

                Console.WriteLine($"正在启动 {service}...");
                var record = await manager.StartAsync(service, dllPath, port, extraArgs);

                using var apiClient = new ManagementApiClient($"http://127.0.0.1:{port}");
                Console.Write($"等待 {service} 就绪");
                try
                {
                    await manager.WaitForReadyAsync(apiClient, checkGatewayHealth: service == "gateway");
                    Console.WriteLine(" 就绪!");
                }
                catch
                {
                    await manager.StopAsync(record, apiClient, CancellationToken.None);
                    throw;
                }

                if (service == "webui")
                    Console.WriteLine($"WebUI 已启动: http://127.0.0.1:{port}（工作区: {workspaceRoot}）");
                else
                    Console.WriteLine($"Gateway 已启动: http://127.0.0.1:{port}（工作区: {workspaceRoot}）");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"启动失败: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

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
