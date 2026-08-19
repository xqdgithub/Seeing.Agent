using System.CommandLine;
using System.Linq;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StartCommand
{
    public static Command Create()
        => CreateCommand("start", "启动指定的服务", fixedService: null);

    public static Command CreateWeb()
        => CreateCommand("web", "启动 WebUI 并打开浏览器", "webui");

    public static Command CreateGateway()
        => CreateCommand("gateway", "启动 Gateway", "gateway");

    private static Command CreateCommand(
        string commandName,
        string description,
        string? fixedService)
    {
        var command = new Command(commandName, description);
        Argument<string>? serviceArg = null;
        if (fixedService is null)
        {
            serviceArg = new Argument<string>("service")
            {
                Description = "要启动的服务: webui 或 gateway"
            };
            command = new Command(commandName, description) { serviceArg };
        }

        command.SetAction(async parseResult =>
        {
            var service = (fixedService ?? parseResult.GetValue<string>(serviceArg!))
                ?? string.Empty;
            await ExecuteStartAsync(service.ToLowerInvariant());
        });

        return command;
    }

    private static async Task ExecuteStartAsync(string service)
    {
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
                if (service == "webui")
                    OpenWebUi(WebUiLaunch.BuildUrl(running[0].Port));
                return;
            }

            var cliDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllName = service == "webui"
                ? "Seeing.Agent.WebUI.dll"
                : "Seeing.Gateway.Server.dll";
            var dllPath = FindDll(cliDir, dllName);

            // WebUI 的端口同时写入命令行参数和环境变量，并由同一个 URL 用于就绪检查。
            // 端口探测与进程真正绑定之间存在竞态，故绑定失败时再尝试后续端口。
            var webUiAllocator = service == "webui" ? new PortAllocator() : null;
            var nextWebUiPort = WebUiLaunch.PreferredPort;
            var webUiRetryCount = 0;
            InstanceRecord? record = null;
            var port = 0;

            while (true)
            {
                port = service == "webui"
                    ? webUiAllocator!.NextAvailable(nextWebUiPort)
                    : GetGatewayPort(workspaceRoot);
                var launchUrl = service == "webui"
                    ? WebUiLaunch.BuildUrl(port)
                    : null;
                var extraArgs = service == "webui"
                    ? WebUiLaunch.BuildArguments(port)
                    : Array.Empty<string>();
                var launchEnvironment = service == "webui"
                    ? WebUiLaunch.BuildEnvironment(port)
                    : null;

                Console.WriteLine($"正在启动 {service}（端口: {port}）...");
                record = await manager.StartAsync(
                    service,
                    dllPath,
                    port,
                    extraArgs,
                    environment: launchEnvironment);
                Console.WriteLine($"{service} 进程已启动（PID: {record.Pid}），日志: {record.LogPath}");

                using var apiClient = new ManagementApiClient(launchUrl ?? $"http://127.0.0.1:{port}");
                Console.Write($"等待 {service} 就绪");
                try
                {
                    await manager.WaitForReadyAsync(
                        apiClient,
                        checkGatewayHealth: service == "gateway",
                        process: record);
                    Console.WriteLine(" 就绪!");
                    break;
                }
                catch
                {
                    var portCollision = service == "webui"
                        && webUiRetryCount < 3
                        && !manager.IsProcessRunning(record)
                        && !webUiAllocator!.IsAvailable(port);

                    await manager.StopAsync(record, apiClient, CancellationToken.None);
                    if (!portCollision)
                        throw;

                    webUiRetryCount++;
                    nextWebUiPort = port + 1;
                    Console.WriteLine(
                        $"端口 {port} 绑定失败，详见日志: {record.LogPath}；改用端口 {nextWebUiPort} 重试...");
                }
            }

            if (record is null)
                throw new InvalidOperationException("服务进程未成功启动");

            if (service == "webui")
            {
                var url = WebUiLaunch.BuildUrl(port);
                Console.WriteLine($"WebUI 已启动: {url} 日志: {record.LogPath}（工作区: {workspaceRoot}）");
                OpenWebUi(url);
            }
            else
            {
                Console.WriteLine($"Gateway 已启动: http://127.0.0.1:{port} 日志: {record.LogPath}（工作区: {workspaceRoot}）");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void OpenWebUi(string url)
    {
        if (BrowserLauncher.TryOpen(url, out var error))
        {
            Console.WriteLine($"已打开浏览器: {url}");
            return;
        }

        Console.Error.WriteLine($"浏览器自动打开失败: {error}；请手动访问 {url}");
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
