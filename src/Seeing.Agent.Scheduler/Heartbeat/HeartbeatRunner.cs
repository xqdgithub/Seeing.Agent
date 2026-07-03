using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Execution;
using Seeing.Agent.Scheduler.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Heartbeat;

/// <summary>心跳执行器</summary>
public sealed class HeartbeatRunner : IHeartbeatRunner
{
    private readonly SchedulerOptionsProvider _optionsProvider;
    private readonly ScheduledAgentRunner _agentRunner;
    private readonly IScheduledJobDispatcher _dispatcher;
    private readonly IWorkspaceProvider _workspace;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<HeartbeatRunner> _logger;

    public HeartbeatRunner(
        SchedulerOptionsProvider optionsProvider,
        ScheduledAgentRunner agentRunner,
        IScheduledJobDispatcher dispatcher,
        IWorkspaceProvider workspace,
        ISessionManager sessionManager,
        ILogger<HeartbeatRunner> logger)
    {
        _optionsProvider = optionsProvider;
        _agentRunner = agentRunner;
        _dispatcher = dispatcher;
        _workspace = workspace;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<JobExecutionResult> RunOnceAsync(CancellationToken ct = default)
    {
        var hb = _optionsProvider.Current.Heartbeat;
        if (!hb.Enabled)
        {
            return new JobExecutionResult
            {
                Success = true,
                Output = "Heartbeat disabled",
                Source = ScheduleSources.Heartbeat
            };
        }

        if (!ActiveHoursChecker.IsInActiveHours(hb.ActiveHours))
        {
            _logger.LogDebug("Heartbeat skipped: outside active hours");
            return new JobExecutionResult
            {
                Success = true,
                Output = "Skipped: outside active hours",
                Source = ScheduleSources.Heartbeat
            };
        }

        var queryPath = Path.Combine(_workspace.WorkspaceRoot, hb.QueryFile);
        if (!File.Exists(queryPath))
        {
            _logger.LogDebug("Heartbeat skipped: query file not found at {Path}", queryPath);
            return new JobExecutionResult
            {
                Success = true,
                Output = "Skipped: query file not found",
                Source = ScheduleSources.Heartbeat
            };
        }

        var queryText = (await File.ReadAllTextAsync(queryPath, ct).ConfigureAwait(false)).Trim();
        if (string.IsNullOrEmpty(queryText))
        {
            _logger.LogDebug("Heartbeat skipped: empty query file");
            return new JobExecutionResult
            {
                Success = true,
                Output = "Skipped: empty query file",
                Source = ScheduleSources.Heartbeat
            };
        }

        var sessionId = await ResolveTargetSessionIdAsync(hb, ct).ConfigureAwait(false);

        var result = await _agentRunner.RunAsync(
            ScheduleSources.Heartbeat,
            queryText,
            hb.Agent,
            sessionId,
            hb.TimeoutSeconds,
            ct).ConfigureAwait(false);

        if (result.Success && !string.IsNullOrEmpty(result.Output) &&
            !string.Equals(hb.Target, HeartbeatTargets.Main, StringComparison.OrdinalIgnoreCase))
        {
            await _dispatcher.DispatchAsync(new DispatchRequest
            {
                Source = ScheduleSources.Heartbeat,
                TaskType = ScheduleTaskTypes.Agent,
                Content = result.Output,
                SessionId = sessionId
            }, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<string> ResolveTargetSessionIdAsync(HeartbeatOptions hb, CancellationToken ct)
    {
        if (string.Equals(hb.Target, HeartbeatTargets.Last, StringComparison.OrdinalIgnoreCase))
        {
            var sessions = await _sessionManager.ListAllAsync(null, ct).ConfigureAwait(false);
            var last = sessions.OrderByDescending(s => s.LastActiveAt).FirstOrDefault();
            if (last != null)
                return last.Id;
        }

        if (string.Equals(hb.Target, HeartbeatTargets.Inbox, StringComparison.OrdinalIgnoreCase))
            return "_inbox";

        return hb.SessionId;
    }
}
