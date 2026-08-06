using System.CommandLine;
using System.Linq;
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
                manager.Registry.PruneDead();

                var instances = manager.Registry.Load()
                    .OrderBy(i => i.Service)
                    .ThenBy(i => i.WorkspaceRoot)
                    .ToList();

                Console.WriteLine("服务状态:");
                Console.WriteLine(new string('-', 70));
                if (instances.Count == 0)
                {
                    Console.WriteLine("  无运行中的服务");
                    return;
                }

                foreach (var inst in instances)
                {
                    Console.WriteLine(
                        $"  {inst.Service,-8} 端口: {inst.Port,-5} PID: {inst.Pid,-7} 启动: {inst.StartedAt:yyyy-MM-dd HH:mm}");
                    Console.WriteLine($"           文件夹: {inst.WorkspaceRoot}");
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
}