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
        var data = context.MergedJobDataMap;
        var jobId = data.GetStringValue(JobDataKeys.JobId) ?? context.JobDetail.Key.Name;
        var runId = data.GetStringValue(JobDataKeys.RunId) ?? Guid.NewGuid().ToString("N");
        
        // 通知监听器任务开始
        _listener?.OnJobStart(jobId, runId);
        
        _logger.LogInformation("===== AgentJob.Execute started for {JobId} =====", jobId);
        _logger.LogInformation("AgentJob: FireTime = {Time}, ScheduledFireTime = {Scheduled}", 
            context.FireTimeUtc.LocalDateTime, context.ScheduledFireTimeUtc?.LocalDateTime);
        var taskType = data.GetStringValue(JobDataKeys.TaskType) ?? ScheduleTaskTypes.Agent;
        var sessionId = data.GetStringValue(JobDataKeys.SessionId) ?? "main";
        var agentId = data.GetStringValue(JobDataKeys.AgentId);
        var prompt = data.GetStringValue(JobDataKeys.Prompt);
        var text = data.GetStringValue(JobDataKeys.Text);
        var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId);
        
        // 使用类型安全的扩展方法读取（自动从字符串转换）
        var timeoutSeconds = data.GetIntValue(JobDataKeys.TimeoutSeconds, SchedulerConstants.DefaultTimeoutSeconds);

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
                TaskType = taskType,
                Source = ScheduleSources.Cron,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                Text = text,
                DispatchSessionId = dispatchSessionId
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
                userInput = data.GetStringValue(JobDataKeys.Text);  // Text 任务的用户输入
            }
            else
            {
                result = await ExecuteAgentJobAsync(data, sessionId, timeoutCts.Token);
                userInput = data.GetStringValue(JobDataKeys.Prompt);  // Agent 任务的用户输入
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
                TaskType = taskType,
                Source = ScheduleSources.Cron,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                Text = text,
                DispatchSessionId = dispatchSessionId
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
                TaskType = taskType,
                Source = ScheduleSources.Cron,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                Text = text,
                DispatchSessionId = dispatchSessionId
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
        var text = data.GetStringValue(JobDataKeys.Text) ?? string.Empty;
        var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId);
        
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JobExecutionResult
            {
                Success = false,
                Error = "Text task has empty content",
                TaskType = ScheduleTaskTypes.Text,
                Source = ScheduleSources.Cron,
                SessionId = sessionId,
                Text = text,
                DispatchSessionId = dispatchSessionId
            };
        }

        return new JobExecutionResult
        {
            Success = true,
            Output = text,
            TaskType = ScheduleTaskTypes.Text,
            Source = ScheduleSources.Cron,
            SessionId = sessionId,
            Text = text,
            DispatchSessionId = dispatchSessionId
        };
    }

    private async Task<JobExecutionResult> ExecuteAgentJobAsync(
        JobDataMap data,
        string sessionId,
        CancellationToken ct)
    {
        var agentId = data.GetStringValue(JobDataKeys.AgentId);
        var prompt = data.GetStringValue(JobDataKeys.Prompt) ?? string.Empty;
        var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new JobExecutionResult
            {
                Success = false,
                Error = "Agent task has empty prompt",
                TaskType = ScheduleTaskTypes.Agent,
                Source = ScheduleSources.Cron,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                DispatchSessionId = dispatchSessionId
            };
        }

        var resolvedAgentId = await _selectionResolver.ResolveAgentIdAsync(agentId, null, ct);
        var agentInstance = _agentRegistry.GetOrCreateAgentInstance(resolvedAgentId)
            ?? throw new InvalidOperationException($"Agent '{resolvedAgentId}' not found");

        var agentDefinition = Core.Models.AgentDefinition.FromAgent(agentInstance);
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
            Agent = resolvedAgentId,
            SessionId = sessionId,
            Prompt = prompt,
            DispatchSessionId = dispatchSessionId
        };
    }

    private async Task DispatchAsync(JobDataMap data, string content, string defaultSessionId, string? userInput, CancellationToken ct)
    {
        var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId) ?? defaultSessionId;

        var dispatchResult = await _dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = ScheduleSources.Cron,
            TaskType = ScheduleTaskTypes.Agent,
            Content = content,
            UserInput = userInput,
            SessionId = dispatchSessionId
        }, ct);

        if (!dispatchResult.Success)
        {
            _logger.LogWarning("Dispatch failed: {Error}", dispatchResult.Error);
        }
    }
}
