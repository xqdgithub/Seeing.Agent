using System.CommandLine;
using System.Diagnostics;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class TuiCommand
{
    public static Command Create()
    {
        var command = new Command("tui", "在当前终端前台启动 TUI（等价于无参数运行）");

        var continueOption = new Option<bool>("--continue", "-c") { Description = "继续最近的会话" };
        var resumeOption = new Option<string?>("--resume") { Description = "恢复指定会话 Id" };
        var agentOption = new Option<string?>("--agent") { Description = "覆盖默认 Agent" };
        var modelOption = new Option<string?>("--model") { Description = "覆盖默认模型" };
        var logOption = new Option<string?>("--log-level") { Description = "日志级别（Debug/Information/Warning/Error）" };
        var bootOption = new Option<string?>("--boot")
        {
            Description = "进程启动能力集（写入子进程 SEEING_BOOT，覆盖 seeing.json Boot）"
        };

        command.Options.Add(continueOption);
        command.Options.Add(resumeOption);
        command.Options.Add(agentOption);
        command.Options.Add(modelOption);
        command.Options.Add(logOption);
        command.Options.Add(bootOption);

        command.SetAction(async parseResult =>
        {
            var options = new TuiForwardOptions(
                Continue: parseResult.GetValue(continueOption),
                Resume: parseResult.GetValue(resumeOption),
                Agent: parseResult.GetValue(agentOption),
                Model: parseResult.GetValue(modelOption),
                LogLevel: parseResult.GetValue(logOption),
                Boot: parseResult.GetValue(bootOption));

            Environment.ExitCode = await ExecuteAsync(options);
        });

        return command;
    }

    /// <summary>
    /// 在当前终端原地启动 seeing-tui：子进程继承控制台，工作目录与工作区均为 CLI 的启动目录。
    /// 工作区经 SEEING_WORKSPACE_ROOT 显式钉到启动目录（覆盖 shell 中可能已存在的同名变量）。
    /// TUI 自行处理 Ctrl+C 与退出确认，故不设宽限期强制终止。
    /// </summary>
    internal static async Task<int> ExecuteAsync(TuiForwardOptions options, CancellationToken ct = default)
    {
        if (!TuiLaunch.CanRunInteractive(Console.IsInputRedirected, Console.IsOutputRedirected))
        {
            Console.Error.WriteLine("当前终端非交互（输入或输出被重定向），无法启动 TUI；可用 seeing-cli --help 查看命令。");
            return 1;
        }

        string dllPath;
        try
        {
            dllPath = ServiceAssetLocator.Find(AppDomain.CurrentDomain.BaseDirectory, TuiLaunch.DllName);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var workspaceRoot = Directory.GetCurrentDirectory();
        var psi = ForegroundProcessRunner.CreateStartInfo(
            dllPath,
            workspaceRoot,
            TuiLaunch.BuildArguments(options),
            TuiLaunch.BuildEnvironment(workspaceRoot, Path.GetDirectoryName(dllPath)));

        using var process = Process.Start(psi);
        if (process is null)
        {
            Console.Error.WriteLine("无法启动 TUI 进程");
            return 1;
        }

        return await ForegroundProcessRunner.RunAsync(process, killOnInterruptAfter: null, ct);
    }
}
