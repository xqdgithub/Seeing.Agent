using System.CommandLine;
using Seeing.Agent.Cli.Commands;

namespace Seeing.Agent.Cli;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Seeing.Agent CLI 管理工具");

        // Subcommands will be added in following tasks
        rootCommand.AddCommand(StartCommand.Create());
        rootCommand.AddCommand(StopCommand.Create());
        rootCommand.AddCommand(StatusCommand.Create());
        rootCommand.AddCommand(ConfigCommand.Create());
        rootCommand.AddCommand(AgentCommand.Create());
        rootCommand.AddCommand(JobCommand.Create());
        rootCommand.AddCommand(SessionCommand.Create());

        return await rootCommand.InvokeAsync(args);
    }
}
