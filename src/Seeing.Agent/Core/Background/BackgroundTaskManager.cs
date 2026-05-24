using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Session.Core;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Text;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台任务管理器实现
/// </summary>
public class BackgroundTaskManager : IBackgroundTaskManager
{
    private readonly ConcurrentDictionary<string, BackgroundTaskInfo> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _outputs = new();
    private readonly ConcurrentDictionary<string, Subject<BackgroundTaskProgress>> _progressSubjects = new();
    private readonly ConcurrentDictionary<string, Subject<string>> _outputSubjects = new();
    private readonly IAgentRegistry _agentRegistry;
    private readonly ISessionManager? _sessionManager;
    private readonly ILogger<BackgroundTaskManager> _logger;

    /// <summary>
    /// 创建后台任务管理器
    /// </summary>
    public BackgroundTaskManager(
        IAgentRegistry agentRegistry,
        ILogger<BackgroundTaskManager> logger,
        ISessionManager? sessionManager = null)
    {
        _agentRegistry = agentRegistry;
        _logger = logger;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// 启动后台任务
    /// </summary>
    public async Task<string> StartAsync(BackgroundTaskLaunchArgs args)
    {
        // 生成任务 ID
        var taskId = $"bg_{Guid.NewGuid():N}".Substring(0, 12);

        // 创建取消令牌源
        var cts = new CancellationTokenSource();
        _cancellationSources[taskId] = cts;

        // 创建任务信息
        var taskInfo = new BackgroundTaskInfo
        {
            Id = taskId,
            AgentName = args.AgentName,
            Status = BackgroundTaskStatus.Pending,
            StartedAt = DateTimeOffset.Now,
            Description = args.Description ?? args.Input.Content,
            SessionId = args.Context.SessionId,
            ParentSessionId = args.Context.SessionId
        };
        _tasks[taskId] = taskInfo;
        _outputs[taskId] = new StringBuilder();

        _logger.LogInformation("后台任务已创建: {TaskId}, Agent: {AgentName}", taskId, args.AgentName);

        // 在后台执行任务
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteTaskAsync(taskId, args, cts.Token);
            }
            catch (OperationCanceledException)
            {
                UpdateTaskStatus(taskId, BackgroundTaskStatus.Cancelled);
                _logger.LogInformation("后台任务已取消: {TaskId}", taskId);
            }
            catch (Exception ex)
            {
                UpdateTaskStatus(taskId, BackgroundTaskStatus.Failed, error: ex.Message);
                _logger.LogError(ex, "后台任务执行失败: {TaskId}", taskId);
            }
        }, CancellationToken.None); // 不传递外部 CancellationToken，使用内部的 cts

        // 等待状态从 Pending 变为 Running（短暂等待）
        await WaitForRunningStatusAsync(taskId, TimeSpan.FromMilliseconds(100));

        return taskId;
    }

    /// <summary>
    /// 获取任务状态
    /// </summary>
    public Task<BackgroundTaskInfo?> GetAsync(string taskId)
    {
        var task = _tasks.TryGetValue(taskId, out var info) ? info : null;
        return Task.FromResult(task);
    }

    /// <summary>
    /// 获取任务输出
    /// </summary>
    public Task<string?> GetOutputAsync(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return Task.FromResult<string?>(null);
        }

        // 如果任务完成，返回完整输出
        if (task.Status == BackgroundTaskStatus.Completed ||
            task.Status == BackgroundTaskStatus.Failed ||
            task.Status == BackgroundTaskStatus.Cancelled)
        {
            return Task.FromResult(task.Result ?? task.Error);
        }

        // 如果任务正在运行，返回当前输出
        var output = _outputs.TryGetValue(taskId, out var sb) ? sb.ToString() : null;
        return Task.FromResult(output);
    }

    /// <summary>
    /// 取消任务
    /// </summary>
    public Task<bool> CancelAsync(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            _logger.LogWarning("尝试取消不存在任务: {TaskId}", taskId);
            return Task.FromResult(false);
        }

        // 只有 Pending 或 Running 状态的任务可以取消
        if (task.Status != BackgroundTaskStatus.Pending &&
            task.Status != BackgroundTaskStatus.Running)
        {
            _logger.LogWarning("任务状态不允许取消: {TaskId}, Status: {Status}", taskId, task.Status);
            return Task.FromResult(false);
        }

        // 请求取消
        if (_cancellationSources.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
            _logger.LogInformation("已请求取消任务: {TaskId}", taskId);
        }

        // 立即更新状态为 Cancelled（如果还没开始执行）
        if (task.Status == BackgroundTaskStatus.Pending)
        {
            UpdateTaskStatus(taskId, BackgroundTaskStatus.Cancelled);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// 取消所有后台任务
    /// </summary>
    public async Task<int> CancelAllAsync()
    {
        var count = 0;
        foreach (var (taskId, cts) in _cancellationSources)
        {
            if (_tasks.TryGetValue(taskId, out var task) &&
                (task.Status == BackgroundTaskStatus.Pending ||
                 task.Status == BackgroundTaskStatus.Running))
            {
                cts.Cancel();
                if (task.Status == BackgroundTaskStatus.Pending)
                {
                    UpdateTaskStatus(taskId, BackgroundTaskStatus.Cancelled);
                }
                count++;
            }
        }

        _logger.LogInformation("已请求取消所有后台任务: {Count}", count);
        return count;
    }

    /// <summary>
    /// 列出所有任务
    /// </summary>
    public Task<IReadOnlyList<BackgroundTaskInfo>> ListAsync(BackgroundTaskStatus? status = null)
    {
        var tasks = _tasks.Values.ToList();

        if (status.HasValue)
        {
            tasks = tasks.Where(t => t.Status == status.Value).ToList();
        }

        // 按开始时间排序（最新的在前）
        tasks.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));

        return Task.FromResult<IReadOnlyList<BackgroundTaskInfo>>(tasks);
    }

    /// <summary>
    /// 等待任务完成
    /// </summary>
    public async Task<BackgroundTaskInfo?> WaitAsync(string taskId, int timeoutMs = 60000)
    {
        // 限制超时时间
        timeoutMs = Math.Min(timeoutMs, 600000);

        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return null;
        }

        var startTime = DateTimeOffset.Now;
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);

        while (DateTimeOffset.Now - startTime < timeout)
        {
            // 检查任务状态
            if (!_tasks.TryGetValue(taskId, out var currentTask))
            {
                return null;
            }

            if (currentTask.Status != BackgroundTaskStatus.Pending &&
                currentTask.Status != BackgroundTaskStatus.Running)
            {
                return currentTask;
            }

            await Task.Delay(1000);
        }

        // 超时后返回当前状态
        _logger.LogWarning("等待任务完成超时: {TaskId}, Timeout: {Timeout}ms", taskId, timeoutMs);
        return _tasks.TryGetValue(taskId, out var finalTask) ? finalTask : null;
    }

    /// <summary>
    /// 执行后台任务
    /// </summary>
    private async Task ExecuteTaskAsync(
        string taskId,
        BackgroundTaskLaunchArgs args,
        CancellationToken cancellationToken)
    {
        // 更新状态为 Running
        UpdateTaskStatus(taskId, BackgroundTaskStatus.Running);
        _logger.LogInformation("后台任务开始执行: {TaskId}", taskId);

        // 获取 Agent 实例
        var agent = _agentRegistry.GetOrCreateAgentInstance(args.AgentName);
        if (agent == null)
        {
            throw new InvalidOperationException($"Agent '{args.AgentName}' 不存在");
        }

        // 执行 Agent
        var outputBuilder = _outputs[taskId];
        try
        {
            await foreach (var message in agent.ExecuteAsync(args.Input, args.Context, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // 收集输出
                if (!string.IsNullOrEmpty(message.Content))
                {
                    outputBuilder.AppendLine(message.Content);
                }

                // 处理工具调用结果
                if (message.ToolCalls?.Count > 0)
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        outputBuilder.AppendLine($"[Tool: {toolCall.Name}]");
                    }
                }
            }

            // 任务完成
            var result = outputBuilder.ToString();
            UpdateTaskStatus(taskId, BackgroundTaskStatus.Completed, result: result);
            _logger.LogInformation("后台任务完成: {TaskId}, OutputLength: {Length}", taskId, result.Length);
        }
        catch (OperationCanceledException)
        {
            UpdateTaskStatus(taskId, BackgroundTaskStatus.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            UpdateTaskStatus(taskId, BackgroundTaskStatus.Failed, error: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 更新任务状态
    /// </summary>
    private void UpdateTaskStatus(
        string taskId,
        BackgroundTaskStatus status,
        string? result = null,
        string? error = null)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = status;
            task.CompletedAt = DateTimeOffset.Now;

            if (result != null)
            {
                task.Result = result;
            }

            if (error != null)
            {
                task.Error = error;
            }

            // 如果任务完成（成功、失败或取消），完成 Subjects
            if (status == BackgroundTaskStatus.Completed ||
                status == BackgroundTaskStatus.Failed ||
                status == BackgroundTaskStatus.Cancelled)
            {
                CompleteSubjects(taskId);
            }
        }
    }

    /// <summary>
    /// 等待任务状态变为 Running
    /// </summary>
    private async Task WaitForRunningStatusAsync(string taskId, TimeSpan timeout)
    {
        var startTime = DateTimeOffset.Now;

        while (DateTimeOffset.Now - startTime < timeout)
        {
            if (_tasks.TryGetValue(taskId, out var task) &&
                task.Status == BackgroundTaskStatus.Running)
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// 清理已完成的任务（可选）
    /// </summary>
    public void CleanupCompletedTasks(TimeSpan retentionTime)
    {
        var cutoff = DateTimeOffset.Now - retentionTime;

        foreach (var (taskId, task) in _tasks)
        {
            if (task.CompletedAt.HasValue && task.CompletedAt.Value < cutoff)
            {
                _tasks.TryRemove(taskId, out _);
                _cancellationSources.TryRemove(taskId, out var cts);
                cts?.Dispose();
                _outputs.TryRemove(taskId, out _);

                // 完成 Subjects
                if (_progressSubjects.TryRemove(taskId, out var progressSubject))
                {
                    progressSubject.OnCompleted();
                    progressSubject.Dispose();
                }
                if (_outputSubjects.TryRemove(taskId, out var outputSubject))
                {
                    outputSubject.OnCompleted();
                    outputSubject.Dispose();
                }

                _logger.LogInformation("清理已完成任务: {TaskId}", taskId);
            }
        }
    }

    // === 新增方法 ===

    /// <summary>订阅任务进度</summary>
    public IObservable<BackgroundTaskProgress> SubscribeProgress(string taskId)
    {
        var subject = _progressSubjects.GetOrAdd(taskId, _ => new Subject<BackgroundTaskProgress>());
        return subject;
    }

    /// <summary>订阅任务输出流</summary>
    public IObservable<string> SubscribeOutput(string taskId)
    {
        var subject = _outputSubjects.GetOrAdd(taskId, _ => new Subject<string>());
        return subject;
    }

    /// <summary>注入任务结果到当前会话</summary>
    public async Task<bool> InjectResultAsync(string taskId, string sessionId, CancellationToken ct = default)
    {
        var task = await GetAsync(taskId);
        if (task == null)
        {
            _logger.LogWarning("任务不存在: {TaskId}", taskId);
            return false;
        }

        if (task.Status != BackgroundTaskStatus.Completed)
        {
            _logger.LogWarning("任务未完成，无法注入: {TaskId}, Status: {Status}", taskId, task.Status);
            return false;
        }

        if (_sessionManager == null)
        {
            _logger.LogWarning("SessionManager 未配置，无法注入结果");
            return false;
        }

        try
        {
            // 创建工具结果消息
            var message = SessionMessage.ToolMessage(
                task.Result ?? task.Error ?? "Task completed with no output",
                toolCallId: $"bg_{taskId}",
                toolName: $"background_{task.AgentName}"
            );

            await _sessionManager.AddMessageAsync(sessionId, message, ct);
            _logger.LogInformation("已注入任务结果到会话: {TaskId} -> {SessionId}", taskId, sessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "注入任务结果失败: {TaskId}", taskId);
            return false;
        }
    }

    /// <summary>报告进度（内部方法）</summary>
    private void ReportProgress(string taskId, int percent, string? message = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;

        task.Progress = Math.Clamp(percent, 0, 100);
        task.ProgressMessage = message;
        task.ProgressUpdatedAt = DateTimeOffset.Now;

        if (_progressSubjects.TryGetValue(taskId, out var subject))
        {
            subject.OnNext(new BackgroundTaskProgress
            {
                TaskId = taskId,
                Percent = task.Progress,
                Message = message,
                Type = ProgressType.Update
            });
        }
    }

    /// <summary>报告输出行（内部方法）</summary>
    private void ReportOutput(string taskId, string line)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;

        task.OutputLines.Add(line);

        if (_outputSubjects.TryGetValue(taskId, out var subject))
        {
            subject.OnNext(line);
        }
    }

    /// <summary>完成任务 Subjects</summary>
    private void CompleteSubjects(string taskId)
    {
        if (_progressSubjects.TryRemove(taskId, out var progressSubject))
        {
            progressSubject.OnNext(new BackgroundTaskProgress
            {
                TaskId = taskId,
                Percent = 100,
                Type = ProgressType.Completed
            });
            progressSubject.OnCompleted();
        }

        if (_outputSubjects.TryRemove(taskId, out var outputSubject))
        {
            outputSubject.OnCompleted();
        }
    }
}