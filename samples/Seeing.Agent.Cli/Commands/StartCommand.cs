using System.CommandLine;
using System.Diagnostics;
using System.Linq;
using Seeing.Agent.Cli.Services;
using Seeing.Agent.Core.Modules;

namespace Seeing.Agent.Cli.Commands;

public static class StartCommand
{
    /// <summary>受支持的启动目标；tui 委派给 <see cref="TuiCommand"/>。</summary>
    public static readonly string[] SupportedServices = { "webui", "gateway", "tui" };

    public static Command Create()
        => CreateCommand("start", "启动指定的服务", fixedService: null);

    public static Command CreateWeb()
        => CreateCommand("web", "在前台启动 WebUI 并打开浏览器（--background 可改为后台）", "webui");

    public static Command CreateGateway()
        => CreateCommand("gateway", "启动 Gateway（默认后台，--foreground 可占用当前终端）", "gateway");

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
                Description = "要启动的服务: webui、gateway 或 tui"
            };
            command = new Command(commandName, description) { serviceArg };
        }

        var bootOption = new Option<string?>("--boot")
        {
            Description =
                "进程启动能力集（写入子进程 SEEING_BOOT，覆盖 seeing.json Boot）。" +
                "内置：* / minimal / code / work / research / full / secure / dev"
        };
        var backgroundOption = new Option<bool>("--background", "-b")
        {
            Description = "后台启动（脱离当前终端，输出写入日志文件）"
        };
        var foregroundOption = new Option<bool>("--foreground", "-f")
        {
            Description = "前台启动（占用当前终端，Ctrl+C 停止）"
        };

        command.Options.Add(bootOption);
        command.Options.Add(backgroundOption);
        command.Options.Add(foregroundOption);

        command.SetAction(async parseResult =>
        {
            var service = (fixedService ?? parseResult.GetValue(serviceArg!))
                ?? string.Empty;
            var boot = parseResult.GetValue(bootOption);
            var background = parseResult.GetValue(backgroundOption);
            var foreground = parseResult.GetValue(foregroundOption);
            Environment.ExitCode = await ExecuteStartAsync(
                service.ToLowerInvariant(), boot, background, foreground);
        });

        return command;
    }

    private static async Task<int> ExecuteStartAsync(
        string service,
        string? boot,
        bool background,
        bool foreground)
    {
        if (!SupportedServices.Contains(service))
        {
            Console.Error.WriteLine("错误: service 必须是 'webui'、'gateway' 或 'tui'");
            return 1;
        }

        var effectiveBoot = string.IsNullOrWhiteSpace(boot)
            ? BootOverrideSource.ResolveFromEnvironment()
            : boot.Trim();

        if (service == "tui")
        {
            return await TuiCommand.ExecuteAsync(new TuiForwardOptions(
                Boot: string.IsNullOrWhiteSpace(effectiveBoot) ? null : effectiveBoot));
        }

        if (!ServiceRunModeResolver.TryResolve(service, background, foreground, out var mode, out var modeError))
        {
            Console.Error.WriteLine($"错误: {modeError}");
            return 1;
        }

        var workspaceRoot = CliWorkspace.Resolve();
        var manager = new ServiceProcessManager(workspaceRoot);
        Process? foregroundProcess = null;
        InstanceRecord? foregroundRecord = null;

        try
        {
            manager.Registry.PruneDead();

            var running = manager.Registry.Load()
                .Where(i => i.Service == service && i.WorkspaceRoot == workspaceRoot)
                .ToList();
            if (running.Count > 0)
            {
                Console.WriteLine($"服务 {service} 已在 {workspaceRoot} 运行中（端口: {running[0].Port}）");
                if (service == "webui")
                    OpenWebUi(WebUiLaunch.BuildUrl(running[0].Port));
                return 0;
            }

            var cliDir = AppDomain.CurrentDomain.BaseDirectory;
            var dllName = service == "webui"
                ? ServiceAssetLocator.WebUiDll
                : ServiceAssetLocator.GatewayDll;
            var dllPath = ServiceAssetLocator.Find(cliDir, dllName);

            // Gateway 的启用状态与端口只能来自项目级 seeing.json：未启用时立即失败，
            // 否则会空等 30 秒就绪并把刚启动的进程杀掉（AddSeeingGatewayServer 忽略 IConfiguration）。
            var gatewayPort = GatewayConfig.DefaultPort;
            if (service == "gateway")
            {
                var (gatewayEnabled, configuredPort) = GatewayConfig.Resolve(workspaceRoot);
                if (!gatewayEnabled)
                {
                    Console.Error.WriteLine(GatewayConfig.BuildDisabledMessage(workspaceRoot));
                    return 1;
                }

                gatewayPort = configuredPort;
            }

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
                    : gatewayPort;
                var launchUrl = service == "webui"
                    ? WebUiLaunch.BuildUrl(port)
                    : null;
                var extraArgs = service == "webui"
                    ? WebUiLaunch.BuildArguments(port)
                    : Array.Empty<string>();
                var launchEnvironment = service == "webui"
                    ? WebUiLaunch.BuildEnvironment(port)
                    : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(effectiveBoot))
                {
                    launchEnvironment[BootOverrideSource.EnvironmentVariableName] = effectiveBoot;
                    Console.WriteLine(
                        $"BootOverride={effectiveBoot}（{BootOverrideSource.EnvironmentVariableName}）");
                }

                var modeLabel = mode == ServiceRunMode.Foreground ? "前台" : "后台";
                Console.WriteLine($"正在{modeLabel}启动 {service}（端口: {port}）...");

                if (mode == ServiceRunMode.Foreground)
                {
                    launchEnvironment["ASPNETCORE_CONTENTROOT"] = Path.GetDirectoryName(dllPath)!;
                    (foregroundProcess, record) = manager.StartForeground(
                        service, dllPath, port, extraArgs, launchEnvironment);
                    foregroundRecord = record;
                    Console.WriteLine($"{service} 进程已启动（PID: {record.Pid}）");
                }
                else
                {
                    record = await manager.StartAsync(
                        service,
                        dllPath,
                        port,
                        extraArgs,
                        environment: launchEnvironment);
                    Console.WriteLine($"{service} 进程已启动（PID: {record.Pid}），日志: {record.LogPath}");
                }

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
                    // 前台模式下进程仍在运行即视为已启动：就绪探测依赖 HTTP 表面，
                    // 对未暴露该表面的服务（如 gateway）不适用；前台输出直接可见，由用户依据日志判断。
                    if (mode == ServiceRunMode.Foreground && manager.IsProcessRunning(record))
                    {
                        Console.WriteLine(" 未确认 HTTP 就绪（进程仍在运行，继续前台等待）");
                        break;
                    }

                    var portCollision = service == "webui"
                        && webUiRetryCount < 3
                        && !manager.IsProcessRunning(record)
                        && !webUiAllocator!.IsAvailable(port);

                    await manager.StopAsync(record, apiClient, CancellationToken.None);
                    if (mode == ServiceRunMode.Foreground)
                    {
                        foregroundProcess?.Dispose();
                        foregroundProcess = null;
                    }

                    if (!portCollision)
                        throw;

                    webUiRetryCount++;
                    nextWebUiPort = port + 1;
                    Console.WriteLine(
                        $"端口 {port} 绑定失败{ServiceProcessManager.DescribeOutput(record)}；改用端口 {nextWebUiPort} 重试...");
                }
            }

            if (record is null)
                return Fail(new InvalidOperationException("服务进程未成功启动"));

            if (service == "webui")
            {
                var url = WebUiLaunch.BuildUrl(port);
                Console.WriteLine($"WebUI 已启动: {url}（工作区: {workspaceRoot}）");
                OpenWebUi(url);
            }
            else
            {
                Console.WriteLine($"Gateway 已启动: http://127.0.0.1:{port}（工作区: {workspaceRoot}）");
            }

            if (mode != ServiceRunMode.Foreground || foregroundProcess is null)
                return 0;

            Console.WriteLine("前台运行中，按 Ctrl+C 停止...");
            try
            {
                return await ForegroundProcessRunner.RunAsync(
                    foregroundProcess,
                    killOnInterruptAfter: TimeSpan.FromSeconds(10));
            }
            finally
            {
                CleanupForeground(manager, foregroundProcess, foregroundRecord);
            }
        }
        catch (Exception ex)
        {
            // 前台实例在「启动成功 → 进入等待」之间也可能抛异常（就绪探测、浏览器打开等），必须兜底回收。
            CleanupForeground(manager, foregroundProcess, foregroundRecord);
            return Fail(ex);
        }
    }

    /// <summary>回收前台实例：注销注册表记录并释放进程对象（重复调用安全）。</summary>
    private static void CleanupForeground(
        ServiceProcessManager manager,
        Process? process,
        InstanceRecord? record)
    {
        if (process is null)
            return;

        if (record is not null)
            manager.Registry.Remove(record.Id);

        process.Dispose();
    }

    private static int Fail(Exception ex)
    {
        Console.Error.WriteLine($"启动失败: {ex.Message}");
        return 1;
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
}
