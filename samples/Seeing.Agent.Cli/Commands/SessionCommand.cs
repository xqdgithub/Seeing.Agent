using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Session.Core;

namespace Seeing.Agent.Cli.Commands;

public static class SessionCommand
{
    public static Command Create()
    {
        var command = new Command("session", "管理会话");

        var listCommand = new Command("list", "列出所有会话");
        listCommand.SetAction(async parseResult =>
        {
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<ISessionManager>();
            var sessions = manager.List();

            Console.WriteLine($"{"SessionId",-38} {"Agent",-20} {"Messages",-10} {"Updated"}");
            Console.WriteLine(new string('-', 92));
            foreach (var s in sessions.OrderByDescending(s => s.UpdatedAt))
            {
                Console.WriteLine($"{s.Id,-38} {s.SelectedAgent ?? "-",-20} {s.Messages.Count,-10} {s.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            }

            if (sessions.Count == 0)
                Console.WriteLine("(无会话)");
        });

        var deleteIdArg = new Argument<string>("id") { Description = "会话 ID" };
        var deleteCommand = new Command("delete", "删除会话") { deleteIdArg };
        deleteCommand.SetAction(async parseResult =>
        {
            var id = parseResult.GetValue<string>(deleteIdArg);
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<ISessionManager>();
            var deleted = manager.Delete(id);

            if (deleted)
                Console.WriteLine($"会话 {id} 已删除");
            else
                Console.WriteLine($"未找到会话: {id}");
        });

        var showIdArg = new Argument<string>("id") { Description = "会话 ID" };
        var showCommand = new Command("show", "查看会话详情") { showIdArg };
        showCommand.SetAction(async parseResult =>
        {
            var id = parseResult.GetValue<string>(showIdArg);
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<ISessionManager>();
            var session = manager.Get(id);

            if (session == null)
            {
                Console.Error.WriteLine($"未找到会话: {id}");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"会话: {session.Id}");
            Console.WriteLine(new string('-', 50));
            Console.WriteLine($"  代理:       {session.SelectedAgent ?? "-"}");
            Console.WriteLine($"  消息数:     {session.Messages.Count}");
            Console.WriteLine($"  创建时间:   {session.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  更新时间:   {session.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  分区:       {session.PartitionId ?? "-"}");
            Console.WriteLine($"  父会话:     {session.ParentSessionId ?? "-"}");
        });

        command.Subcommands.Add(listCommand);
        command.Subcommands.Add(deleteCommand);
        command.Subcommands.Add(showCommand);

        return command;
    }
}
