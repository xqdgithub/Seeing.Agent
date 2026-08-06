using System.CommandLine;
using Seeing.Agent.Cli.Commands;

namespace Seeing.Agent.Cli;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Seeing.Agent CLI 管理工具");

        // Subcommands will be added in following tasks
        rootCommand.Subcommands.Add(InstallCommand.Create());
        rootCommand.Subcommands.Add(StartCommand.Create());
        rootCommand.Subcommands.Add(StopCommand.Create());
        rootCommand.Subcommands.Add(StatusCommand.Create());
        rootCommand.Subcommands.Add(ConfigCommand.Create());
        rootCommand.Subcommands.Add(AgentCommand.Create());
        rootCommand.Subcommands.Add(JobCommand.Create());
        rootCommand.Subcommands.Add(SessionCommand.Create());

        return await rootCommand.Parse(args).InvokeAsync();
    }
}
