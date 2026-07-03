using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Scheduler.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Execution;

/// <summary>共享 Agent 执行辅助 — 经 IAgentExecutionRouter 自动路由 ACP/Native</summary>
public sealed class ScheduledAgentRunner
{
    private readonly IAgentExecutionRouter _executionRouter;
    private readonly IAgentRegistry _agentRegistry;
    private readonly AgentSelectionResolver _selectionResolver;
    private readonly IWorkspaceProvider _workspace;
    private readonly IServiceProvider _services;
    private readonly HookManager _hooks;
    private readonly ILogger<ScheduledAgentRunner> _logger;

    public ScheduledAgentRunner(
        IAgentExecutionRouter executionRouter,
        IAgentRegistry agentRegistry,
        AgentSelectionResolver selectionResolver,
        IWorkspaceProvider workspace,
        IServiceProvider services,
        HookManager hooks,
        ILogger<ScheduledAgentRunner> logger)
    {
        _executionRouter = executionRouter;
        _agentRegistry = agentRegistry;
        _selectionResolver = selectionResolver;
        _workspace = workspace;
        _services = services;
        _hooks = hooks;
        _logger = logger;
    }

    /// <summary>执行 Agent 任务</summary>
    public async Task<JobExecutionResult> RunAsync(
        string source,
        string prompt,
        string? agentId,
        string sessionId,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        var hookSpec = source == ScheduleSources.Heartbeat
            ? HookRegistry.SchedulerHeartbeatBefore
            : HookRegistry.SchedulerJobBeforeExecute;

        var hookResult = await _hooks.TriggerBlockingAsync(
            hookSpec,
            sessionId,
            new Dictionary<string, object?>
            {
                ["source"] = source,
                ["sessionId"] = sessionId,
                ["agentName"] = agentId
            },
            cancellationToken: timeoutCts.Token).ConfigureAwait(false);

        if (!hookResult.Continue)
        {
            return new JobExecutionResult
            {
                Success = false,
                Error = hookResult.Error?.Message ?? "Execution blocked by hook",
                Source = source,
                SessionId = sessionId
            };
        }

        try
        {
            var resolvedAgentId = await _selectionResolver.ResolveAgentIdAsync(agentId, null, timeoutCts.Token)
                .ConfigureAwait(false);

            var agentInstance = _agentRegistry.GetOrCreateAgentInstance(resolvedAgentId)
                ?? throw new InvalidOperationException($"Agent '{resolvedAgentId}' not found");

            var agentDefinition = AgentDefinition.FromAgent(agentInstance);
            var workspaceRoot = _workspace.WorkspaceRoot;

            var context = new AgentContext
            {
                SessionId = sessionId,
                CancellationToken = timeoutCts.Token,
                Services = _services,
                WorkingDirectory = workspaceRoot,
                WorkspaceRoot = workspaceRoot,
                IsBackground = true,
                History = new List<ChatMessage>
                {
                    new() { Role = ChatRole.User, Content = prompt }
                },
                Metadata = new Dictionary<string, object>
                {
                    ["source"] = source
                }
            };

            var output = new StringBuilder();
            var hasError = false;
            string? errorMessage = null;

            await foreach (var evt in _executionRouter.ExecuteAsync(agentDefinition, context, timeoutCts.Token)
                               .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case StreamCompleteEvent complete when !string.IsNullOrEmpty(complete.Message.Content):
                        output.AppendLine(complete.Message.Content);
                        break;
                    case ErrorEvent error:
                        hasError = true;
                        errorMessage = error.Message;
                        break;
                }
            }

            var afterHook = source == ScheduleSources.Heartbeat
                ? HookRegistry.SchedulerHeartbeatAfter
                : HookRegistry.SchedulerJobAfterExecute;

            _hooks.TriggerFireAndForget(afterHook, sessionId, new Dictionary<string, object?>
            {
                ["source"] = source,
                ["sessionId"] = sessionId,
                ["agentName"] = resolvedAgentId,
                ["success"] = !hasError
            });

            return new JobExecutionResult
            {
                Success = !hasError,
                Output = output.ToString().Trim(),
                Error = errorMessage,
                TaskType = ScheduleTaskTypes.Agent,
                Source = source,
                AgentName = resolvedAgentId,
                SessionId = sessionId
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new JobExecutionResult
            {
                Success = false,
                Error = $"Execution timed out after {timeoutSeconds}s",
                Source = source,
                SessionId = sessionId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled agent run failed (source={Source}, session={SessionId})", source, sessionId);
            return new JobExecutionResult
            {
                Success = false,
                Error = ex.Message,
                Source = source,
                SessionId = sessionId
            };
        }
    }
}
