using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Execution;

/// <summary>定时任务执行器</summary>
public sealed class AgentScheduledJobExecutor : IScheduledJobExecutor
{
    private readonly ScheduledAgentRunner _agentRunner;
    private readonly IScheduledJobDispatcher _dispatcher;
    private readonly ILogger<AgentScheduledJobExecutor> _logger;

    public AgentScheduledJobExecutor(
        ScheduledAgentRunner agentRunner,
        IScheduledJobDispatcher dispatcher,
        ILogger<AgentScheduledJobExecutor> logger)
    {
        _agentRunner = agentRunner;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<JobExecutionResult> ExecuteAsync(ScheduledJobSpec job, CancellationToken ct = default)
    {
        var sessionId = job.Dispatch.Target.SessionId ?? "main";

        if (job.TaskType == ScheduleTaskTypes.Text)
        {
            var text = (job.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                return new JobExecutionResult
                {
                    Success = false,
                    Error = "Text task has empty content",
                    TaskType = ScheduleTaskTypes.Text,
                    Source = ScheduleSources.Cron,
                    SessionId = sessionId
                };
            }

            await DispatchAsync(job, text, ct).ConfigureAwait(false);
            return new JobExecutionResult
            {
                Success = true,
                Output = text,
                TaskType = ScheduleTaskTypes.Text,
                Source = ScheduleSources.Cron,
                SessionId = sessionId
            };
        }

        var prompt = (job.Prompt ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            return new JobExecutionResult
            {
                Success = false,
                Error = "Agent task has empty prompt",
                TaskType = ScheduleTaskTypes.Agent,
                Source = ScheduleSources.Cron,
                SessionId = sessionId
            };
        }

        var result = await _agentRunner.RunAsync(
            ScheduleSources.Cron,
            prompt,
            job.Agent,
            sessionId,
            job.Runtime.TimeoutSeconds,
            ct).ConfigureAwait(false);

        if (result.Success && !string.IsNullOrEmpty(result.Output))
            await DispatchAsync(job, result.Output, ct).ConfigureAwait(false);

        return result;
    }

    private async Task DispatchAsync(ScheduledJobSpec job, string content, CancellationToken ct)
    {
        var dispatchResult = await _dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Cron,
            TaskType = job.TaskType,
            Content = content,
            Channel = job.Dispatch.Target.Channel,
            UserId = job.Dispatch.Target.UserId,
            SessionId = job.Dispatch.Target.SessionId,
            Metadata = job.Dispatch.Meta
        }, ct).ConfigureAwait(false);

        if (!dispatchResult.Success)
            _logger.LogWarning("Dispatch failed for job {JobId}: {Error}", job.Id, dispatchResult.Error);
    }
}
