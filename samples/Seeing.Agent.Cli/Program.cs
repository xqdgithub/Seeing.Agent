using System.CommandLine;
using Seeing.Agent.Cli.Commands;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        // 无参数 = 在启动目录原地进入 TUI；`tui` / `start tui` 为等价的显式入口。
        if (args.Length == 0)
            return await TuiCommand.ExecuteAsync(new TuiForwardOptions());

        var rootCommand = new RootCommand("Seeing.Agent CLI 管理工具");

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
