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

        // 执行画像：缺键等于 cron。心跳与定时任务共用本 Job，身份差异全部由画像承载。
        var profile = data.GetJsonValue<ScheduleRunProfile>(JobDataKeys.RunProfile)
                      ?? ScheduleRunProfile.Cron;

        // 通知监听器任务开始
        _listener?.OnJobStart(jobId, runId);

        _logger.LogInformation("===== AgentJob.Execute started for {JobId} =====", jobId);
        _logger.LogInformation("AgentJob: FireTime = {Time}, ScheduledFireTime = {Scheduled}",
            context.FireTimeUtc.LocalDateTime, context.ScheduledFireTimeUtc?.LocalDateTime);
        var taskType = data.GetStringValue(JobDataKeys.TaskType) ?? ScheduleTaskTypes.Agent;
        var configuredSessionId = data.GetStringValue(JobDataKeys.SessionId);
        var agentId = data.GetStringValue(JobDataKeys.AgentId);
        var prompt = data.GetStringValue(JobDataKeys.Prompt);
        var text = data.GetStringValue(JobDataKeys.Text);
        var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId);

        // 使用类型安全的扩展方法读取（自动从字符串转换）
        var timeoutSeconds = data.GetIntValue(JobDataKeys.TimeoutSeconds, SchedulerConstants.DefaultTimeoutSeconds);

        var ct = context.CancellationToken;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // 目标矩阵解析：显式会话 + conversation（挂绑）/ 仅 conversation（每次触发新建会话）/ 现状
        var sessionId = await ScheduleConversationResolver.ResolveSessionIdAsync(
            _services,
            data,
            jobId,
            data.GetStringValue(JobDataKeys.JobName),
            configuredSessionId,
            profile,
            timeoutCts.Token).ConfigureAwait(false);

        // 触发执行前 Hook
        var hookResult = await _hooks.TriggerBlockingAsync(
            HookRegistry.SchedulerJobBeforeExecute,
            sessionId,
            new Dictionary<string, object?>
            {
                ["source"] = profile.Source,
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
                Source = profile.Source,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                Text = text,
                DispatchSessionId = dispatchSessionId
            };

            // 触发执行后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerJobAfterExecute, sessionId, new Dictionary<string, object?>
            {
                ["source"] = profile.Source,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["success"] = false,
                ["error"] = "Blocked by hook"
            });

            // 通知监听器
            await NotifyExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
            return;
        }

        try
        {
            JobExecutionResult result;
            string? userInput = null;

            if (taskType == ScheduleTaskTypes.Text)
            {
                result = await ExecuteTextJobAsync(data, sessionId, profile, timeoutCts.Token);
                userInput = data.GetStringValue(JobDataKeys.Text);  // Text 任务的用户输入
            }
            else
            {
                result = await ExecuteAgentJobAsync(data, sessionId, profile, timeoutCts.Token);
                userInput = data.GetStringValue(JobDataKeys.Prompt);  // Agent 任务的用户输入
            }

            // 始终投递结果到 Session（确保执行结果可查询）
            // 优先使用 DispatchSessionId，否则使用执行上下文的 sessionId
            if (result.Success && !string.IsNullOrEmpty(result.Output))
            {
                var wrappedInput = WrapJobUserInput(userInput, profile);
                await DispatchAsync(data, result.Output, sessionId, wrappedInput, profile, timeoutCts.Token);
            }

            context.Result = result;

            // 触发执行后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerJobAfterExecute, sessionId, new Dictionary<string, object?>
            {
                ["source"] = profile.Source,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["success"] = result.Success,
                ["error"] = result.Error
            });

            // 通知监听器
            // 单独守一层：监听器负责落盘历史，它失败不该把「已经跑成功」的执行
            // 改判成失败。现场日志里的 "execution failed" 多数就是这条路径造成的
            // （任务输出正常、只是 .tmp 写入失败），读者会误以为是任务本身出错。
            await NotifyExecutedAsync(jobId, result, timeoutCts.Token);
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
                Source = profile.Source,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                Text = text,
                DispatchSessionId = dispatchSessionId
            };

            await NotifyExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} execution failed", jobId);
            context.Result = new JobExecutionResult
            {
                Success = false,
                Error = ex.Message,
                TaskType = taskType,
                Source = profile.Source,
                SessionId = sessionId,
                Agent = agentId,
                Prompt = prompt,
                Text = text,
                DispatchSessionId = dispatchSessionId
            };

            // 触发执行后 Hook
            _hooks.TriggerFireAndForget(HookRegistry.SchedulerJobAfterExecute, sessionId, new Dictionary<string, object?>
            {
                ["source"] = profile.Source,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["success"] = false,
                ["error"] = ex.Message
            });

            // 通知监听器
            await NotifyExecutedAsync(jobId, (JobExecutionResult)context.Result, ct);
        }
    }

    /// <summary>
    /// 通知监听器任务已执行完成。
    /// <para>
    /// 监听器（<c>ScheduleManager</c>）在这里落盘执行历史。落盘属于**旁路记账**，
    /// 它的失败不能反向改写任务本身的成败：原先直接 await，一旦
    /// <c>jobs_history/*.json.tmp</c> 写入失败就会被外层 <c>catch</c> 捕获，
    /// 把「已成功」的结果覆盖成 <c>Success = false</c> 并打出 "execution failed"，
    /// 读者看着像 Agent 执行出错，实际只是记账失败。
    /// </para>
    /// <para>
    /// 因此这里吞掉异常并降级为 Warning：执行结果保持原样，历史缺失可接受
    /// （下次追加会重写整份文件，不会扩散）。
    /// </para>
    /// </summary>
    private async Task NotifyExecutedAsync(string jobId, JobExecutionResult result, CancellationToken ct)
    {
        if (_listener is null)
            return;

        try
        {
            await _listener.OnJobExecutedAsync(jobId, result, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "执行历史落盘失败（任务本身结果为 {Status}，不受影响）: {JobId}",
                result.Success ? "success" : "failed",
                jobId);
        }
    }

    private async Task<JobExecutionResult> ExecuteTextJobAsync(
        JobDataMap data,
        string sessionId,
        ScheduleRunProfile profile,
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
                Source = profile.Source,
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
            Source = profile.Source,
            SessionId = sessionId,
            Text = text,
            DispatchSessionId = dispatchSessionId
        };
    }

    private async Task<JobExecutionResult> ExecuteAgentJobAsync(
        JobDataMap data,
        string sessionId,
        ScheduleRunProfile profile,
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
                Source = profile.Source,
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
            ["source"] = profile.Source
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
                    profile.ReminderKind)
            }
        };

        // schema 单点纪律：直连执行必须携带会话级结算后的真实工具 schema，
        // 否则 LLM 请求无 tools，模型只能靠文本「表演」工具调用。
        var resolution = await ResolveToolSchemasAsync(_services, sessionId, agentDefinition, _logger, ct)
            .ConfigureAwait(false);

        // 旁路只读上下文装配：填充执行作用域（cwd / 工作区根 / 钉住快照）后装配，
        // 不插入消息、不写会话状态、不保存会话——执行未经由会话运行时，不得改变主链事实。
        var assembly = await SchedulerContextAssembly.TryAssembleAsync(
            _services, sessionId, cwd, projectRoot, _logger, ct).ConfigureAwait(false);

        var context = new AgentContext
        {
            SessionId = sessionId,
            CancellationToken = ct,
            Services = _services,
            WorkingDirectory = cwd,
            WorkspaceRoot = projectRoot,
            IsBackground = true,
            ToolSettlement = resolution,
            ContextBaseline = AppendCatalogToBaseline(assembly.SystemBaseline, resolution),
            SkillSnapshot = assembly.SkillSnapshot,
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
            Source = profile.Source,
            Agent = resolvedAgentId,
            SessionId = sessionId,
            Prompt = prompt,
            DispatchSessionId = dispatchSessionId
        };
    }

    /// <summary>
    /// 旁路的目录补偿：目录已改为 <c>Target = Message</c> 的上下文源，而旁路走只读装配
    /// （不插入消息），因此必须把同一结算产出的目录正文拼进系统基线，
    /// 否则旁路模型既看不到目录、也看不到参数签名（相对改动前是回退）。
    /// <para>旁路自建消息列表、不写会话状态，因此不违反单一写入者契约。</para>
    /// </summary>
    public static string AppendCatalogToBaseline(
        string baseline, SessionToolSchemaResolution? resolution)
    {
        if (resolution is null || string.IsNullOrWhiteSpace(resolution.CatalogText))
            return baseline;

        return string.IsNullOrWhiteSpace(baseline)
            ? resolution.CatalogText
            : baseline + "\n\n" + resolution.CatalogText;
    }

    /// <summary>
    /// 工具视图单点纪律（AgentJob / HeartbeatJob 共用）：直连 <see cref="IAgentExecutor"/> 的旁路执行
    /// 经 <see cref="ISessionToolSchemaResolver"/> 取本轮工具视图；未注册或解析失败时空集继续（不阻断任务）。
    /// <para>
    /// 旁路**不提交**结算状态（不写度量、不改升级位），但会先提升上一轮登记的待生效发现，
    /// 否则 cron 会话里 tool_search 的发现永远不会生效。
    /// </para>
    /// </summary>
    internal static async Task<SessionToolSchemaResolution?> ResolveToolSchemasAsync(
        IServiceProvider services,
        string sessionId,
        AgentDefinition agent,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // 轮首提升：cron 会话里 tool_search 的发现必须在下一次执行内联（与主链同语义）。
        // 会话不存在/加载失败时端口返回 false 且不抛，保持“旁路不阻断任务”。
        var exposureWriter = services.GetService<ISessionToolExposureWriter>();
        if (exposureWriter is null)
        {
            logger.LogWarning(
                "ISessionToolExposureWriter 未注册：Scheduler 旁路的发现不会被提升，tool_search 命中将无法在下次执行内联: SessionId={SessionId}",
                sessionId);
        }
        else
        {
            await exposureWriter.PromotePendingAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

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
            return await resolver.ResolveAsync(sessionId, agent, cancellationToken).ConfigureAwait(false);
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

    private string? WrapJobUserInput(string? userInput, ScheduleRunProfile profile)
    {
        if (string.IsNullOrEmpty(userInput))
            return userInput;
        if (_reminders.TryParse(userInput, out _))
            return userInput;
        return _reminders.Wrap(
            userInput,
            SystemReminder.Sources.Job,
            profile.ReminderKind);
    }

    private async Task DispatchAsync(
        JobDataMap data,
        string content,
        string defaultSessionId,
        string? userInput,
        ScheduleRunProfile profile,
        CancellationToken ct)
    {
        var dispatchSessionId = data.GetStringValue(JobDataKeys.DispatchSessionId) ?? defaultSessionId;

        var dispatchResult = await _dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = profile.Source,
            TaskType = ScheduleTaskTypes.Agent,
            Content = content,
            UserInput = userInput,
            SessionId = dispatchSessionId,
            ReminderKind = profile.ReminderKind,
            SenderId = profile.SenderId,
            Layout = profile.Layout,
            // 任务级 conversation 出站目标（ChannelDeliveryScheduleDispatcher 优先于会话 ChannelRoute）
            Conversation = ScheduleConversationResolver.ReadConversation(data)
        }, ct);

        if (!dispatchResult.Success)
        {
            _logger.LogWarning("Dispatch failed: {Error}", dispatchResult.Error);
        }
    }
}
