using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Commands;
using Seeing.Agent.Scheduler.Abstractions;

namespace Seeing.Agent.Scheduler.Commands;

/// <summary>启动时注册 cron 命令</summary>
internal sealed class SchedulerCommandRegistrationHostedService : IHostedService
{
    private readonly ICommandRegistry _registry;
    private readonly IScheduleManager _manager;
    private readonly IHeartbeatRunner _heartbeatRunner;
    private readonly ILogger<SchedulerCommandRegistrationHostedService> _logger;

    public SchedulerCommandRegistrationHostedService(
        ICommandRegistry registry,
        IScheduleManager manager,
        IHeartbeatRunner heartbeatRunner,
        ILogger<SchedulerCommandRegistrationHostedService> logger)
    {
        _registry = registry;
        _manager = manager;
        _heartbeatRunner = heartbeatRunner;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registry.Register(new CronListCommand(_manager));
        _registry.Register(new CronRunCommand(_manager));
        _registry.Register(new HeartbeatRunCommand(_heartbeatRunner));
        _logger.LogDebug("Scheduler commands registered");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CronListCommand : ICommand
{
    private readonly IScheduleManager _manager;

    public CronListCommand(IScheduleManager manager) => _manager = manager;

    public CommandMetadata Metadata => CommandMetadata.Simple(
        "cron-list",
        "列出所有定时任务",
        "/cron-list",
        CommandCategory.System);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var jobs = await _manager.ListJobsAsync(cancellationToken).ConfigureAwait(false);
        if (jobs.Count == 0)
            return CommandResult.Ok("No scheduled jobs.");

        var lines = jobs.Select(j =>
            $"- {j.Id} [{j.TaskType}] enabled={j.Enabled} agent={j.Agent ?? "-"} next={j.NextRunAt?.ToString("u") ?? "-"}");
        return CommandResult.Ok(string.Join(Environment.NewLine, lines));
    }
}

internal sealed class CronRunCommand : ICommand
{
    private readonly IScheduleManager _manager;

    public CronRunCommand(IScheduleManager manager) => _manager = manager;

    public CommandMetadata Metadata => CommandMetadata.Simple(
        "cron-run",
        "立即执行指定定时任务",
        "/cron-run <jobId>",
        CommandCategory.System);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var jobId = context.Arguments.Trim();
        if (string.IsNullOrEmpty(jobId))
            return CommandResult.Fail("Usage: /cron-run <jobId>");

        var result = await _manager.RunJobOnceAsync(jobId, cancellationToken).ConfigureAwait(false);
        return result.Success
            ? CommandResult.Ok(result.Output ?? "Job completed.")
            : CommandResult.Fail(result.Error ?? "Job failed.");
    }
}

internal sealed class HeartbeatRunCommand : ICommand
{
    private readonly IHeartbeatRunner _heartbeatRunner;

    public HeartbeatRunCommand(IHeartbeatRunner heartbeatRunner) => _heartbeatRunner = heartbeatRunner;

    public CommandMetadata Metadata => CommandMetadata.Simple(
        "heartbeat-run",
        "立即执行一次心跳",
        "/heartbeat-run",
        CommandCategory.System);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var result = await _heartbeatRunner.RunOnceAsync(cancellationToken).ConfigureAwait(false);
        return result.Success
            ? CommandResult.Ok(result.Output ?? "Heartbeat completed.")
            : CommandResult.Fail(result.Error ?? "Heartbeat failed.");
    }
}
