using Seeing.Agent.Cli.Commands;
using Seeing.Agent.Cli.Services;
using Seeing.Agent.HostComposition;
using System.CommandLine;

namespace Seeing.Agent.Cli;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        // 宿主子模式：客户端会用「当前进程的可执行文件」拉起它，
        // 因此每个入口都必须在解析自己的命令行之前把这两个开关接走。
        if (args.Contains(BrowserHostRunner.BrowserHostSwitch, StringComparer.Ordinal)
            || args.Contains(BrowserHostRunner.WatchdogSwitch, StringComparer.Ordinal))
        {
            return await BrowserHostRunner.RunAsync(args, CancellationToken.None);
        }

        // 无参数 = 在启动目录原地进入 TUI；`tui` / `start tui` 为等价的显式入口。
        if (args.Length == 0)
            return await TuiCommand.ExecuteAsync(new TuiForwardOptions());

        var rootCommand = new RootCommand("Seeing.Agent CLI 管理工具");

        // 一次性提示词执行（-p）：挂在根命令上，与子命令互不干扰；无参数仍进 TUI。
        PromptCommand.AttachTo(rootCommand);

        rootCommand.Subcommands.Add(InstallCommand.Create());
        rootCommand.Subcommands.Add(StartCommand.Create());
        rootCommand.Subcommands.Add(StartCommand.CreateWeb());
        rootCommand.Subcommands.Add(StartCommand.CreateGateway());
        rootCommand.Subcommands.Add(TuiCommand.Create());
        rootCommand.Subcommands.Add(StopCommand.Create());
        rootCommand.Subcommands.Add(StatusCommand.Create());
        rootCommand.Subcommands.Add(ConfigCommand.Create());
        rootCommand.Subcommands.Add(AgentCommand.Create());
        rootCommand.Subcommands.Add(JobCommand.Create());
        rootCommand.Subcommands.Add(SessionCommand.Create());

        var exitCode = await rootCommand.Parse(args).InvokeAsync();

        // 命令动作通过 Environment.ExitCode 表达失败时，Main 的返回值不能把它吞掉。
        return Environment.ExitCode != 0 ? Environment.ExitCode : exitCode;
    }
}
