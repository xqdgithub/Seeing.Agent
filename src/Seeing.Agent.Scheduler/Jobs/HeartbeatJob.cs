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
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>心跳任务 Job</summary>
[DisallowConcurrentExecution]
public class HeartbeatJob : IJob
{
    private readonly IAgentExecutionRouter _executionRouter;
    private readonly IAgentRegistry _agentRegistry;
    private readonly AgentSelectionResolver _selectionResolver;
    private readonly IWorkspaceProvider _workspace;
    private readonly ISessionManager _sessionManager;
    private readonly IServiceProvider _services;
    private readonly HookManager _hooks;
    private readonly IScheduledJobDispatcher _dispatcher;
    private readonly IJobExecutionListener? _listener;
    private readonly ILogger<HeartbeatJob> _logger;

    public HeartbeatJob(
        IAgentExecutionRouter executionRouter,
        IAgentRegistry agentRegistry,
        AgentSelectionResolver selectionResolver,
        IWorkspaceProvider workspace,
        ISessionManager sessionManager,
        IServiceProvider services,
        HookManager hooks,
        IScheduledJobDispatcher dispatcher,
        IEnumerable<IJobExecutionListener> listeners,
        ILogger<HeartbeatJob> logger)
    {
        _executionRouter = executionRouter;
        _agentRegistry = agentRegistry;
        _selectionResolver = selectionResolver;
        _workspace = workspace;
        _sessionManager = sessionManager;
        _services = services;
        _hooks = hooks;
        _dispatcher = dispatcher;
        _listener = listeners.FirstOrDefault();
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        var jobId = SchedulerConstants.HeartbeatJobId;
        var sessionId = data.GetStringValue(JobDataKeys.SessionId) ?? "main";
        
        // 使用类型安全的扩展方法读取（自动从字符串转换）
        var timeoutSeconds = data.GetIntValue(JobDataKeys.TimeoutSeconds, SchedulerConstants.DefaultTimeoutSeconds);

        var ct = context.CancellationToken;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // 检查活跃时段
        var activeHours = data.GetJsonValue<ActiveHoursOptions>(JobDataKeys.ActiveHours);
        if (activeHours != null && !ActiveHoursChecker.IsInActiveHours(activeHours))
        {
            _logger.LogDebug("Heartbeat skipped: outside active hours");
            context.Result = new JobExecutionResult
            {
                Success = true,
                Output = "Skipped: outside active hours",
                Source = ScheduleSources.Heartbeat,
                SessionId = sessionId
            };

            _hooks.TriggerFireAndForget(HookRegistry.SchedulerHeartbeatAfter, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Heartbeat,
                ["sessionId"] = sessionId,
                ["success"] = true,
                ["error"] = "Outside active hours"
            });
            return;
        }

        // 触发心跳前 Hook
        var hookResult = await _hooks.TriggerBlockingAsync(
            HookRegistry.SchedulerHeartbeatBefore,
            sessionId,
            new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Heartbeat,
                ["sessionId"] = sessionId
            },
            cancellationToken: timeoutCts.Token);

        if (!hookResult.Continue)
        {
            _logger.LogWarning("Heartbeat blocked by hook: {Error}", hookResult.Error?.Message);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = hookResult.Error?.Message ?? "Blocked by hook",
                Source = ScheduleSources.Heartbeat,
                SessionId = sessionId
            };

            _hooks.TriggerFireAndForget(HookRegistry.SchedulerHeartbeatAfter, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Heartbeat,
                ["sessionId"] = sessionId,
                ["success"] = false,
                ["error"] = "Blocked by hook"
            });
            return;
        }

        try
        {
            // 获取 Prompt（必填）
            var queryText = data.GetStringValue(JobDataKeys.Prompt)?.Trim();
            
            if (string.IsNullOrEmpty(queryText))
            {
                _logger.LogWarning("Heartbeat skipped: prompt is empty or not configured");
                context.Result = new JobExecutionResult
                {
                    Success = false,
                    Error = "Heartbeat prompt is required but not configured",
                    Source = ScheduleSources.Heartbeat,
                    SessionId = sessionId
                };

                _hooks.TriggerFireAndForget(HookRegistry.SchedulerHeartbeatAfter, sessionId, new Dictionary<string, object?>
                {
                    ["source"] = ScheduleSources.Heartbeat,
                    ["sessionId"] = sessionId,
                    ["success"] = false,
                    ["error"] = "Prompt not configured"
                });
                
                if (_listener != null)
                {
                    await _listener.OnJobExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
                }
                return;
            }

            // 解析目标 Session
            var target = data.GetStringValue(JobDataKeys.HeartbeatTarget) ?? HeartbeatTargets.Main;
            sessionId = await ResolveTargetSessionIdAsync(target, sessionId, timeoutCts.Token);

            // 执行 Agent
            var agentId = data.GetStringValue(JobDataKeys.AgentId);
            var result = await ExecuteAgentAsync(queryText, agentId, sessionId, timeoutCts.Token);

            // 投递结果（非 main 目标）
            if (result.Success && !string.IsNullOrEmpty(result.Output) &&
                !string.Equals(target, HeartbeatTargets.Main, StringComparison.OrdinalIgnoreCase))
            {
                await _dispatcher.DispatchAsync(new DispatchRequest
                {
                    Source = ScheduleSources.Heartbeat,
                    TaskType = ScheduleTaskTypes.Agent,
                    Content = result.Output,
                    UserInput = queryText,  // 保存用户查询作为输入
                    SessionId = sessionId
                }, timeoutCts.Token);
            }

            context.Result = result;

            // 触发心跳后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerHeartbeatAfter, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Heartbeat,
                ["sessionId"] = sessionId,
                ["success"] = result.Success
            });

            // 通知监听器
            if (_listener != null)
            {
                await _listener.OnJobExecutedAsync(jobId, result, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Heartbeat timed out after {Timeout}s", timeoutSeconds);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = $"Execution timed out after {timeoutSeconds}s",
                Source = ScheduleSources.Heartbeat,
                SessionId = sessionId
            };

            // 触发心跳后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerHeartbeatAfter, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Heartbeat,
                ["sessionId"] = sessionId,
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
            _logger.LogError(ex, "Heartbeat execution failed: {Message}", ex.Message);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                Source = ScheduleSources.Heartbeat,
                SessionId = sessionId
            };

            // 触发心跳后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerHeartbeatAfter, sessionId, new Dictionary<string, object?>
            {
                ["source"] = ScheduleSources.Heartbeat,
                ["sessionId"] = sessionId,
                ["success"] = false,
                ["error"] = ex.Message
            });

            // 通知监听器 - 确保在异常时也调用
            if (_listener != null)
            {
                try
                {
                    await _listener.OnJobExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
                }
                catch (Exception listenerEx)
                {
                    _logger.LogError(listenerEx, "Error in job execution listener");
                }
            }
        }
    }

    private async Task<string> ResolveTargetSessionIdAsync(string target, string defaultSessionId, CancellationToken ct)
    {
        if (string.Equals(target, HeartbeatTargets.Last, StringComparison.OrdinalIgnoreCase))
        {
            var sessions = await _sessionManager.ListAllAsync(null, ct);
            var last = sessions.OrderByDescending(s => s.LastActiveAt).FirstOrDefault();
            if (last != null)
                return last.Id;
        }

        if (string.Equals(target, HeartbeatTargets.Inbox, StringComparison.OrdinalIgnoreCase))
            return "_inbox";

        return defaultSessionId;
    }

    private async Task<JobExecutionResult> ExecuteAgentAsync(
        string prompt,
        string? agentId,
        string sessionId,
        CancellationToken ct)
    {
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
                ["source"] = ScheduleSources.Heartbeat
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
            Source = ScheduleSources.Heartbeat,
            Agent = resolvedAgentId,
            SessionId = sessionId
        };
    }
}