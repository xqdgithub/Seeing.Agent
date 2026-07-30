using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Cli.Commands;

public static class JobCommand
{
    public static Command Create()
    {
        var command = new Command("job", "管理定时任务");

        var listCommand = new Command("list", "列出所有定时任务");
        listCommand.SetHandler(async () =>
        {
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            var statuses = await manager.GetAllJobStatusesAsync();

            Console.WriteLine($"{"JobId",-25} {"State",-12} {"NextRun",-22} {"LastRun",-22}");
            Console.WriteLine(new string('-', 80));
            foreach (var s in statuses)
            {
                var next = s.NextFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                var prev = s.PreviousFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                Console.WriteLine($"{s.JobId,-25} {s.State,-12} {next,-22} {prev,-22}");
            }
        });

        var runJobArg = new Argument<string>("jobId", "任务 ID");
        var runCommand = new Command("run", "立即执行一个任务") { runJobArg };
        runCommand.SetHandler(async (jobId) =>
        {
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            var result = await manager.RunJobOnceAsync(jobId);

            Console.WriteLine(result switch
            {
                TriggerResult.Accepted a => $"任务已触发，RunId: {a.RunId}",
                TriggerResult.NotFound => "任务未找到",
                TriggerResult.Disabled => "任务已禁用",
                TriggerResult.Conflict c => $"冲突: {c.Reason}",
                _ => "未知结果"
            });
        }, runJobArg);

        var disableJobArg = new Argument<string>("jobId", "任务 ID");
        var disableCommand = new Command("disable", "暂停任务") { disableJobArg };
        disableCommand.SetHandler(async (jobId) =>
        {
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            await manager.DisableJobAsync(jobId);
            Console.WriteLine($"任务 {jobId} 已禁用");
        }, disableJobArg);

        var enableJobArg = new Argument<string>("jobId", "任务 ID");
        var enableCommand = new Command("enable", "恢复任务") { enableJobArg };
        enableCommand.SetHandler(async (jobId) =>
        {
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            await manager.ResumeJobAsync(jobId);
            Console.WriteLine($"任务 {jobId} 已恢复");
        }, enableJobArg);

        command.AddCommand(listCommand);
        command.AddCommand(runCommand);
        command.AddCommand(disableCommand);
        command.AddCommand(enableCommand);

        return command;
    }
}
