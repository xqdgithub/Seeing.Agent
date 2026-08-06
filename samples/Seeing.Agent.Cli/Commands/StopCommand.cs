using System.CommandLine;
using System.Linq;
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
                manager.Registry.PruneDead();

                var candidates = manager.Registry.Load()
                    .Where(i => i.Service == service)
                    .ToList();
                if (candidates.Count == 0)
                {
                    Console.WriteLine($"服务 {service} 未在运行");
                    return;
                }

                InstanceRecord target;
                if (candidates.Count == 1)
                {
                    target = candidates[0];
                }
                else
                {
                    Console.WriteLine($"找到 {candidates.Count} 个 {service} 实例，选择要关闭的编号:");
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var c = candidates[i];
                        Console.WriteLine(
                            $"  [{i + 1}] 文件夹: {c.WorkspaceRoot}  端口: {c.Port}  PID: {c.Pid}  启动: {c.StartedAt:yyyy-MM-dd HH:mm}");
                    }
                    Console.Write("编号: ");
                    var input = Console.ReadLine();
                    if (!int.TryParse(input, out var idx) || idx < 1 || idx > candidates.Count)
                    {
                        Console.Error.WriteLine("无效选择");
                        Environment.ExitCode = 1;
                        return;
                    }
                    target = candidates[idx - 1];
                }

                using var apiClient = new ManagementApiClient($"http://127.0.0.1:{target.Port}");
                Console.WriteLine($"正在停止 {service}（文件夹: {target.WorkspaceRoot}）...");
                var ok = await manager.StopAsync(target, apiClient, CancellationToken.None);

                Console.WriteLine(ok ? $"{service} 已停止" : $"{service} 已强制终止");
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
}