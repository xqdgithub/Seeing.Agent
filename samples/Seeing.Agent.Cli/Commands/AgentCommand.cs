using Seeing.Agent.Abstractions.Agents;
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Cli.Infrastructure;

using Seeing.Agent.Abstractions.Agents;
namespace Seeing.Agent.Cli.Commands;

public static class AgentCommand
{
    public static Command Create()
    {
        var command = new Command("agent", "管理代理");

        var listCommand = new Command("list", "列出所有代理");
        listCommand.SetAction(async parseResult =>
        {
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var registry = host.Services.GetRequiredService<IAgentRegistry>();
            var agents = await registry.GetAgentsAsync();

            Console.WriteLine($"{"Name",-20} {"Mode",-12} {"Runtime",-10} {"Model",-15} {"Status"}");
            Console.WriteLine(new string('-', 76));
            foreach (var a in agents)
            {
                var model = a.Model?.ToString() ?? "-";
                var status = a.Disabled ? "Disabled" : "Active";
                Console.WriteLine($"{a.Name,-20} {a.Mode,-12} {a.Runtime,-10} {model,-15} {status}");
            }
        });

        var showNameArg = new Argument<string>("name") { Description = "代理名称" };
        var showCommand = new Command("show", "查看代理详情") { showNameArg };
        showCommand.SetAction(async parseResult =>
        {
            var name = parseResult.GetValue<string>(showNameArg);
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var registry = host.Services.GetRequiredService<IAgentRegistry>();
            var agent = await registry.GetAgentAsync(name);

            if (agent == null)
            {
                Console.Error.WriteLine($"未找到代理: {name}");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"代理: {agent.Name}");
            Console.WriteLine(new string('-', 50));
            Console.WriteLine($"  描述:         {agent.Description ?? "-"}");
            Console.WriteLine($"  模式:         {agent.Mode}");
            Console.WriteLine($"  运行时:       {agent.Runtime}");
            Console.WriteLine($"  模型:         {agent.Model?.ToString() ?? "-"}");
            Console.WriteLine($"  状态:         {(agent.Disabled ? "Disabled" : "Active")}");
            Console.WriteLine($"  最大步数:     {agent.MaxSteps?.ToString() ?? "-"}");
            Console.WriteLine($"  ACP 后端:     {agent.AcpBackend ?? "-"}");
            Console.WriteLine($"  允许的工具:   {(agent.AllowedTools.Count > 0 ? string.Join(", ", agent.AllowedTools) : "(全部)")}");
            Console.WriteLine($"  禁止的工具:   {(agent.DeniedTools.Count > 0 ? string.Join(", ", agent.DeniedTools) : "(无)")}");
            Console.WriteLine($"  权限规则数:   {agent.PermissionRules.Count}");
            Console.WriteLine($"  系统提示词:   {(string.IsNullOrEmpty(agent.SystemPrompt) ? "(无)" : $"{agent.SystemPrompt[..Math.Min(100, agent.SystemPrompt.Length)]}...")}");
        });

        command.Subcommands.Add(listCommand);
        command.Subcommands.Add(showCommand);

        return command;
    }
}
