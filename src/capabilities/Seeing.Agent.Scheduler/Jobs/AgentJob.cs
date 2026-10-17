using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using System.Text;

namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>Agent 任务执行 Job</summary>
[DisallowConcurrentExecution]
public class AgentJob : IJob
{
    private readonly IAgentExecutor _executionRouter;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentSelectionResolver _selectionResolver;
    private readonly IWorkspaceProvider _workspace;
    private readonly IExecutionWorld _world;
    private readonly IServiceProvider _services;
    private readonly IHookManager _hooks;
    private readonly ISystemReminderRenderer _reminders;
    private readonly IScheduledJobDispatcher _dispatcher;
    private readonly IJobExecutionListener? _listener;
    private readonly ILogger<AgentJob> _logger;

    public AgentJob(
        IAgentExecutor executionRouter,
        IAgentRegistry agentRegistry,
        IAgentSelectionResolver selectionResolver,
        IWorkspaceProvider workspace,
        IExecutionWorld world,
        IServiceProvider services,
        IHookManager hooks,
        ISystemReminderRenderer reminders,
        IScheduledJobDispatcher dispatcher,
        IEnumerable<IJobExecutionListener> listeners,
        ILogger<AgentJob> logger)
    {
        _executionRouter = executionRouter;
        _agentRegistry = agentRegistry;
        _selectionResolver = selectionResolver;
        _workspace = workspace;
        _world = world;
        _services = services;
        _hooks = hooks;
        _reminders = reminders;
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
                var wrappedInput = WrapJobUserInput(userInput);
                await DispatchAsync(data, result.Output, sessionId, wrappedInput, timeoutCts.Token);
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
        catch (OperationCanceledException)
        {
            // 用户取消或 Circuit 断开，正常退出即可
            _logger.LogInformation("Job {JobId} was cancelled", jobId);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = "Cancelled",
                TaskType = taskType,
                Source = ScheduleSources.Cron,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                Text = text,
                DispatchSessionId = dispatchSessionId
            };

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
        var agentDefinition = await _agentRegistry.GetAgentAsync(resolvedAgentId)
            ?? throw new InvalidOperationException($"Agent '{resolvedAgentId}' not found");

        var cwd = _world.Cwd;
        var projectRoot = _workspace.GetProjectRoot();

        var model = data.GetStringValue(JobDataKeys.Model);
        var mode = data.GetStringValue(JobDataKeys.Mode);

        var metadata = new Dictionary<string, object>
        {
            ["source"] = ScheduleSources.Cron
        };
        if (!string.IsNullOrEmpty(model))
            metadata[AgentContextKeys.RequestModelId] = model;
        if (!string.IsNullOrEmpty(mode))
            metadata[AgentContextKeys.AcpModeId] = mode;

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.User,
                Content = _reminders.Wrap(
                    prompt,
                    SystemReminder.Sources.Job,
                    SystemReminder.Kinds.Cron)
            }
        };

        // schema 单点纪律：直连执行必须携带会话级结算后的真实工具 schema，
        // 否则 LLM 请求无 tools，模型只能靠文本「表演」工具调用
        var toolSchemas = await ResolveToolSchemasAsync(_services, sessionId, agentDefinition, _logger, ct)
            .ConfigureAwait(false);

        var context = new AgentContext
        {
            SessionId = sessionId,
            CancellationToken = ct,
            Services = _services,
            WorkingDirectory = cwd,
            WorkspaceRoot = projectRoot,
            IsBackground = true,
            ToolSchemas = toolSchemas,
            Metadata = metadata
        };

        var output = new StringBuilder();
        var hasError = false;
        string? errorMessage = null;

        await foreach (var evt in _executionRouter.ExecuteAsync(agentDefinition, messages, context, ct))
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

    /// <summary>
    /// schema 单点纪律（AgentJob / HeartbeatJob 共用）：直连 <see cref="IAgentExecutor"/> 的旁路执行
    /// 经 <see cref="ISessionToolSchemaResolver"/> 解析会话级工具 schema；未注册或解析失败时空集继续（不阻断任务）。
    /// </summary>
    internal static async Task<IReadOnlyList<FunctionToolSchema>?> ResolveToolSchemasAsync(
        IServiceProvider services,
        string sessionId,
        AgentDefinition agent,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var resolver = services.GetService<ISessionToolSchemaResolver>();
        if (resolver is null)
        {
            logger.LogWarning(
                "ISessionToolSchemaResolver 未注册：任务执行将无工具 schema（模型无法真实调用工具）: SessionId={SessionId}, Agent={Agent}",
                sessionId, agent.Name);
            return null;
        }

        try
        {
            var resolution = await resolver.ResolveAsync(sessionId, agent, cancellationToken).ConfigureAwait(false);
            return resolution.ToolSchemas;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "工具 schema 解析失败，任务执行将以空 schema 继续: SessionId={SessionId}, Agent={Agent}",
                sessionId,
                agent.Name);
            return null;
        }
    }

    private string? WrapJobUserInput(string? userInput)
    {
        if (string.IsNullOrEmpty(userInput))
            return userInput;
        if (_reminders.TryParse(userInput, out _))
            return userInput;
        return _reminders.Wrap(
            userInput,
            SystemReminder.Sources.Job,
            SystemReminder.Kinds.Cron);
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
