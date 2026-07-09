using System.Text;
using Microsoft.Extensions.Logging;
using Quartz;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>Agent 任务执行 Job</summary>
[DisallowConcurrentExecution]
public class AgentJob : IJob
{
    private readonly IAgentExecutionRouter _executionRouter;
    private readonly IAgentRegistry _agentRegistry;
    private readonly AgentSelectionResolver _selectionResolver;
    private readonly IWorkspaceProvider _workspace;
    private readonly IServiceProvider _services;
    private readonly HookManager _hooks;
    private readonly IScheduledJobDispatcher _dispatcher;
    private readonly IJobExecutionListener? _listener;
    private readonly ILogger<AgentJob> _logger;

    public AgentJob(
        IAgentExecutionRouter executionRouter,
        IAgentRegistry agentRegistry,
        AgentSelectionResolver selectionResolver,
        IWorkspaceProvider workspace,
        IServiceProvider services,
        HookManager hooks,
        IScheduledJobDispatcher dispatcher,
        IEnumerable<IJobExecutionListener> listeners,
        ILogger<AgentJob> logger)
    {
        _executionRouter = executionRouter;
        _agentRegistry = agentRegistry;
        _selectionResolver = selectionResolver;
        _workspace = workspace;
        _services = services;
        _hooks = hooks;
        _dispatcher = dispatcher;
        _listener = listeners.FirstOrDefault();
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.JobDetail.JobDataMap;
        var jobId = data.GetString(JobDataKeys.JobId) ?? context.JobDetail.Key.Name;
        var taskType = data.GetString(JobDataKeys.TaskType) ?? ScheduleTaskTypes.Agent;
        var sessionId = data.GetString(JobDataKeys.SessionId) ?? "main";
        var timeoutSeconds = data.GetInt(JobDataKeys.TimeoutSeconds);
        if (timeoutSeconds <= 0) timeoutSeconds = SchedulerConstants.DefaultTimeoutSeconds;

        var ct = context.CancellationToken;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // 触发执行前 Hook
        var hookResult = await _hooks.TriggerBlockingAsync(
            HookRegistry.SchedulerJobBeforeExecute,
            sessionId,
            new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Cron,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId
            },
            cancellationToken: timeoutCts.Token);

        if (!hookResult.Continue)
        {
            _logger.LogWarning("Job {JobId} blocked by hook: {Error}", jobId, hookResult.Error?.Message);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = hookResult.Error?.Message ?? "Blocked by hook",
                Source = ScheduleSources.Cron,
                SessionId = sessionId
            };

            // 触发执行后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerJobAfterExecute, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Cron,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["success"] = false,
                ["error"] = "Blocked by hook"
            });

            // 通知监听器
            if (_listener != null)
            {
                await _listener.OnJobExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
            }
            return;
        }

        try
        {
            JobExecutionResult result;
            string? userInput = null;

            if (taskType == ScheduleTaskTypes.Text)
            {
                result = await ExecuteTextJobAsync(data, sessionId, timeoutCts.Token);
                userInput = data.GetString(JobDataKeys.Text);  // Text 任务的用户输入
            }
            else
            {
                result = await ExecuteAgentJobAsync(data, sessionId, timeoutCts.Token);
                userInput = data.GetString(JobDataKeys.Prompt);  // Agent 任务的用户输入
            }

            // 始终投递结果到 Session（确保执行结果可查询）
            // 优先使用 DispatchSessionId，否则使用执行上下文的 sessionId
            if (result.Success && !string.IsNullOrEmpty(result.Output))
            {
                await DispatchAsync(data, result.Output, sessionId, userInput, timeoutCts.Token);
            }

            context.Result = result;

            // 触发执行后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerJobAfterExecute, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Cron,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["success"] = result.Success,
                ["error"] = result.Error
            });

            // 通知监听器
            if (_listener != null)
            {
                await _listener.OnJobExecutedAsync(jobId, result, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Job {JobId} timed out after {Timeout}s", jobId, timeoutSeconds);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = $"Execution timed out after {timeoutSeconds}s",
                Source = ScheduleSources.Cron,
                SessionId = sessionId
            };

            // 触发执行后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerJobAfterExecute, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Cron,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["success"] = false,
                ["error"] = "Timed out"
            });

            // 通知监听器
            if (_listener != null)
            {
                await _listener.OnJobExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} execution failed", jobId);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = ex.Message,
                Source = ScheduleSources.Cron,
                SessionId = sessionId
            };

            // 触发执行后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerJobAfterExecute, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Cron,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["success"] = false,
                ["error"] = ex.Message
            });

            // 通知监听器
            if (_listener != null)
            {
                await _listener.OnJobExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
            }
        }
    }

    private async Task<JobExecutionResult> ExecuteTextJobAsync(
        JobDataMap data,
        string sessionId,
        CancellationToken ct)
    {
        var text = data.GetString(JobDataKeys.Text) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
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

        return new JobExecutionResult
        {
            Success = true,
            Output = text,
            TaskType = ScheduleTaskTypes.Text,
            Source = ScheduleSources.Cron,
            SessionId = sessionId
        };
    }

    private async Task<JobExecutionResult> ExecuteAgentJobAsync(
        JobDataMap data,
        string sessionId,
        CancellationToken ct)
    {
        var agentId = data.GetString(JobDataKeys.AgentId);
        var prompt = data.GetString(JobDataKeys.Prompt) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(prompt))
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

        var resolvedAgentId = await _selectionResolver.ResolveAgentIdAsync(agentId, null, ct);
        var agentInstance = _agentRegistry.GetOrCreateAgentInstance(resolvedAgentId)
            ?? throw new InvalidOperationException($"Agent '{resolvedAgentId}' not found");

        var agentDefinition = AgentDefinition.FromAgent(agentInstance);
        var workspaceRoot = _workspace.WorkspaceRoot;

        var context = new AgentContext
        {
            SessionId = sessionId,
            CancellationToken = ct,
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
                ["source"] = ScheduleSources.Cron
            }
        };

        var output = new StringBuilder();
        var hasError = false;
        string? errorMessage = null;

        await foreach (var evt in _executionRouter.ExecuteAsync(agentDefinition, context, ct))
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

        return new JobExecutionResult
        {
            Success = !hasError,
            Output = output.ToString().Trim(),
            Error = errorMessage,
            TaskType = ScheduleTaskTypes.Agent,
            Source = ScheduleSources.Cron,
            AgentName = resolvedAgentId,
            SessionId = sessionId
        };
    }

    private async Task DispatchAsync(JobDataMap data, string content, string defaultSessionId, string? userInput, CancellationToken ct)
    {
        // 安全获取可选的投递配置
        string? dispatchChannel = null;
        string? dispatchUserId = null;
        string? dispatchSessionId = defaultSessionId;

        if (data.ContainsKey(JobDataKeys.DispatchChannel))
            dispatchChannel = data.GetString(JobDataKeys.DispatchChannel);
        if (data.ContainsKey(JobDataKeys.DispatchUserId))
            dispatchUserId = data.GetString(JobDataKeys.DispatchUserId);
        if (data.ContainsKey(JobDataKeys.DispatchSessionId))
            dispatchSessionId = data.GetString(JobDataKeys.DispatchSessionId) ?? defaultSessionId;

        var dispatchResult = await _dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Cron,
            TaskType = ScheduleTaskTypes.Agent,
            Content = content,
            UserInput = userInput,
            Channel = dispatchChannel,
            UserId = dispatchUserId,
            SessionId = dispatchSessionId
        }, ct);

        if (!dispatchResult.Success)
        {
            _logger.LogWarning("Dispatch failed: {Error}", dispatchResult.Error);
        }
    }
}
