using Seeing.Agent.Core.Events;
using Seeing.Agent.App.Events;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Core;
using Seeing.Agent.TokenBudget.Api.Responses;
using Seeing.Agent.TokenBudget;
using Seeing.Agent.Tools.BuiltIn.Todo;
using System.Text;

namespace Seeing.Agent.WebUI.Services
{
    /// <summary>
    /// Agent Loop 信息 - 用于 UI 渲染分组
    /// </summary>
    public class AgentLoopInfo
    {
        /// <summary>Loop ID</summary>
        public string LoopId { get; set; } = string.Empty;

        /// <summary>开始时间</summary>
        public DateTime StartTime { get; set; }

        /// <summary>结束时间（完成后设置）</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>是否已完成</summary>
        public bool IsComplete { get; set; }

        /// <summary>总步数</summary>
        public int TotalSteps { get; set; }

        /// <summary>是否成功</summary>
        public bool Success { get; set; }

        /// <summary>错误信息</summary>
        public string? Error { get; set; }

        /// <summary>用户输入（触发消息）</summary>
        public string? UserInput { get; set; }
    }

    /// <summary>
    /// 事件流处理器 - 处理 Core 层消息事件并更新 SessionState
    /// <para>
    /// 已重构：使用 Core 层统一事件类型（IMessageEvent），支持工具状态流转
    /// </para>
    /// <para>
    /// 支持 LoopId 分组：一次 Agent 交互中产生的所有事件通过 LoopId 关联，
    /// 便于前端按对话单元渲染。
    /// </para>
    /// </summary>
    public class EventStreamHandler
    {
        private readonly SessionState _sessionState;
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// 当前助手消息（用于流式增量）
        /// </summary>
        private SessionMessage? _currentAssistantMessage;

        /// <summary>
        /// 当前 Loop 累积的思考内容（跨多轮 LLM 调用）
        /// </summary>
        private readonly StringBuilder _accumulatedReasoning = new();

        /// <summary>
        /// 当前 Loop 累积的内容（跨多轮 LLM 调用）
        /// </summary>
        private readonly StringBuilder _accumulatedContent = new();

        /// <summary>
        /// 当前 Loop ID
        /// </summary>
        private string? _currentLoopId;

        /// <summary>
        /// 当前 Loop 信息
        /// </summary>
        private AgentLoopInfo? _currentLoop;

        /// <summary>
        /// 当前 Step 索引
        /// </summary>
        private int _currentStep = 0;

        /// <summary>
        /// 工具调用的内容位置（UI 层自行管理）
        /// Key: ToolCallId, Value: 工具调用首次出现时的内容长度
        /// </summary>
        private readonly Dictionary<string, int> _toolCallPositions = new();

        /// <summary>
        /// Task 卡片索引：taskId → SessionToolCall（后台完成后仍可按 taskId 更新）
        /// </summary>
        private readonly Dictionary<string, SessionToolCall> _taskIndex = new(StringComparer.Ordinal);

        /// <summary>
        /// 待处理的权限请求
        /// </summary>
        private readonly Dictionary<string, PermissionRequestViewModel> _pendingPermissions = new();

        /// <summary>
        /// UI 更新回调
        /// </summary>
        public event Action? OnStateChanged;

        /// <summary>
        /// Loop 完成回调（用于 UI 渲染优化）
        /// </summary>
        public event Action<AgentLoopInfo>? OnLoopComplete;

        /// <summary>
        /// 权限请求回调
        /// </summary>
        public event Action<PermissionRequestEvent>? OnPermissionRequest;

        /// <summary>
        /// 权限响应回调
        /// </summary>
        public event Action<PermissionResponseEvent>? OnPermissionResponse;

        public EventStreamHandler(SessionState sessionState, ISessionManager sessionManager)
        {
            _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        }

        /// <summary>
        /// 获取当前 Loop ID
        /// </summary>
        public string? GetCurrentLoopId() => _currentLoopId;

        /// <summary>
        /// 获取当前 Loop 信息
        /// </summary>
        public AgentLoopInfo? GetCurrentLoop() => _currentLoop;

        /// <summary>
        /// 处理 Core 层消息事件（仅驱动 UI 展示；会话落盘由 ExecutionJobService 完成）
        /// </summary>
        public Task ProcessEventAsync(IMessageEvent evt)
        {
            EnsureLiveSessionSynced();

            switch (evt.Type)
            {
                case MessageEventType.LoopStart:
                    // ExecutionStartedEvent 也复用 LoopStart 类型，不可按 Type 强转
                    if (evt is LoopStartEvent loopStart)
                        HandleLoopStart(loopStart);
                    break;

                case MessageEventType.LoopComplete:
                    // ExecutionCompleteEvent 也复用 LoopComplete 类型，不可按 Type 强转
                    if (evt is LoopCompleteEvent loopComplete)
                        HandleLoopComplete(loopComplete);
                    break;

                case MessageEventType.StreamStart:
                    HandleStreamStart((StreamStartEvent)evt);
                    break;

                case MessageEventType.StreamDelta:
                    HandleStreamDelta((StreamDeltaEvent)evt);
                    break;

                case MessageEventType.StreamComplete:
                    HandleStreamComplete((StreamCompleteEvent)evt);
                    break;

                case MessageEventType.ToolCallPending:
                case MessageEventType.ToolCallRunning:
                case MessageEventType.ToolCallComplete:
                    HandleToolCall((ToolCallEvent)evt);
                    break;

                case MessageEventType.TaskStarted:
                    HandleTaskStarted((TaskStartedEvent)evt);
                    break;

                case MessageEventType.TaskProgress:
                    HandleTaskProgress((TaskProgressEvent)evt);
                    break;

                case MessageEventType.TaskCompleted:
                    HandleTaskCompleted((TaskCompletedEvent)evt);
                    break;

                case MessageEventType.TaskFailed:
                    HandleTaskFailed((TaskFailedEvent)evt);
                    break;

                case MessageEventType.SubAgentStarted:
                case MessageEventType.SubAgentCompleted:
#pragma warning disable CS0618
                    HandleSubAgent((SubAgentEvent)evt);
#pragma warning restore CS0618
                    break;

                case MessageEventType.PermissionRequest:
                    HandlePermissionRequest((PermissionRequestEvent)evt);
                    break;

                case MessageEventType.PermissionResponse:
                    HandlePermissionResponse((PermissionResponseEvent)evt);
                    break;

                case MessageEventType.LoopCancelled:
                    HandleLoopCancelled((LoopCancelledEvent)evt);
                    break;

                case MessageEventType.Error:
                    HandleError((ErrorEvent)evt);
                    break;

                case MessageEventType.BudgetStatus:
                    HandleBudgetStatus((BudgetStatusEvent)evt);
                    break;

                case MessageEventType.BudgetWarning:
                    HandleBudgetWarning((BudgetWarningEvent)evt);
                    break;

                case MessageEventType.Compaction:
                    HandleCompaction((CompactionEvent)evt);
                    break;

                // App 层扩展事件类型
                case (MessageEventType)AppEventType.SkillContent:
                    HandleSkillContent((SkillContentEvent)evt);
                    break;

                case MessageEventType.TodoUpdate:
                    HandleTodoUpdate((TodoUpdateEvent)evt);
                    break;

                case MessageEventType.ModeUpdate:
                    HandleModeUpdate((ModeUpdateEvent)evt);
                    break;

                default:
                    break;
            }

            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 确保 UI SessionState 与 SessionManager 缓存是同一实例，避免 JobService Save 存到另一份对象
        /// </summary>
        private void EnsureLiveSessionSynced()
        {
            var uiSession = _sessionState.CurrentSession;
            if (uiSession == null || string.IsNullOrEmpty(uiSession.Id))
                return;

            var cached = _sessionManager.Get(uiSession.Id);
            if (cached == null)
            {
                _sessionManager.Register(uiSession);
                return;
            }

            if (!ReferenceEquals(cached, uiSession))
            {
                // 执行路径以 SessionManager 缓存为准；把 UI 指针切回同一实例
                _sessionState.CurrentSession = cached;
                if (_currentAssistantMessage != null &&
                    !string.IsNullOrEmpty(_currentAssistantMessage.Id))
                {
                    var same = cached.Messages.FirstOrDefault(m =>
                        m.Id == _currentAssistantMessage.Id && m.Role == "assistant");
                    if (same != null)
                        _currentAssistantMessage = same;
                }
            }
        }

        /// <summary>
        /// 处理 Loop 开始事件
        /// </summary>
        private void HandleLoopStart(LoopStartEvent evt)
        {
            _currentLoopId = evt.LoopId;
            _currentLoop = new AgentLoopInfo
            {
                LoopId = evt.LoopId,
                StartTime = evt.Timestamp,
                UserInput = evt.UserInput
            };

            // 清空当前助手消息，准备接收新的 Loop
            _currentAssistantMessage = null;
            _toolCallPositions.Clear();
            // 不清空 _taskIndex：后台 Task 可能在 Loop 结束后仍推送 Progress/Completed
            _currentStep = 0;  // ✅ 重置 step，新 Loop 从 0 开始

            // 清空累积缓冲区
            _accumulatedReasoning.Clear();
            _accumulatedContent.Clear();
        }

        /// <summary>
        /// 处理 Loop 完成事件
        /// </summary>
        private void HandleLoopComplete(LoopCompleteEvent evt)
        {
            if (_currentLoop != null)
            {
                _currentLoop.EndTime = evt.Timestamp;
                _currentLoop.IsComplete = true;
                _currentLoop.TotalSteps = evt.TotalSteps;
                _currentLoop.Success = evt.Success;
                _currentLoop.Error = evt.Error;

                // 触发 Loop 完成回调
                OnLoopComplete?.Invoke(_currentLoop);
            }
        }

        /// <summary>
        /// 处理流式开始事件 - 新轮次开始，清空状态
        /// </summary>
        private void HandleStreamStart(StreamStartEvent evt)
        {
            // 更新当前 LoopId（如果事件中有）
            if (!string.IsNullOrEmpty(evt.LoopId))
            {
                _currentLoopId = evt.LoopId;
            }

            // 新轮次开始，清空当前助手消息，准备接收新一轮的 delta
            // 注意：step > 0 时表示这是工具调用后的后续轮次，需要创建新的消息
            if (evt.Step > 0 || _currentAssistantMessage == null)
            {
                // 保存当前消息的 step（用于创建新消息时设置正确的 step）
                _currentStep = evt.Step;
                _currentAssistantMessage = null;
                _toolCallPositions.Clear();
            }
        }

        private void HandleStreamDelta(StreamDeltaEvent evt)
        {
            // 内容由服务端 ChatEventTracker 写入同一 SessionData；UI 只绑定指针用于刷新
            if (!string.IsNullOrEmpty(evt.LoopId))
                _currentLoopId = evt.LoopId;

            EnsureCurrentAssistantMessage(evt.SessionId);

            if (!string.IsNullOrEmpty(evt.ContentDelta))
                _accumulatedContent.Append(evt.ContentDelta);
            if (!string.IsNullOrEmpty(evt.ReasoningDelta))
                _accumulatedReasoning.Append(evt.ReasoningDelta);
        }

        private void HandleStreamComplete(StreamCompleteEvent evt)
        {
            if (evt.Message?.Role == "tool")
                return;

            EnsureCurrentAssistantMessage(evt.SessionId);
        }

        /// <summary>
        /// 处理工具调用事件 - 支持状态流转
        /// </summary>
        private void HandleToolCall(ToolCallEvent evt)
        {
            if (_currentAssistantMessage == null)
            {
                // 如果没有当前消息，尝试从 SessionState 获取最后一条助手消息
                if (_sessionState.CurrentSession?.Messages != null)
                {
                    _currentAssistantMessage = _sessionState.CurrentSession.Messages
                        .Where(m => m.Role == "assistant")
                        .LastOrDefault();
                }
                
                if (_currentAssistantMessage == null)
                {
                    return;
                }
            }

            // 设置 LoopId
            if (!string.IsNullOrEmpty(evt.LoopId))
            {
                _currentAssistantMessage.LoopId = evt.LoopId;
            }

            // 确保工具调用列表存在
            if (_currentAssistantMessage.ToolCalls == null)
            {
                _currentAssistantMessage.ToolCalls = new List<SessionToolCall>();
            }

            // 查找或创建工具调用
            var toolCall = _currentAssistantMessage.ToolCalls.Find(t => t.Id == evt.ToolCallId);
            if (toolCall == null)
            {
                // UI 层自行记录内容位置：在工具调用首次创建时记录当前内容长度
                var contentPosition = _currentAssistantMessage.Content?.Length ?? 0;

                var toolCallId = evt.ToolCallId ?? Guid.NewGuid().ToString();
                toolCall = new SessionToolCall
                {
                    Id = toolCallId,
                    Name = evt.ToolName ?? string.Empty,
                    Arguments = FormatArgumentsJson(evt.Arguments)
                };
                _currentAssistantMessage.ToolCalls.Add(toolCall);

                // 记录该工具调用的内容位置（用于渲染）
                _toolCallPositions[toolCallId] = contentPosition;
            }
            else if (string.IsNullOrEmpty(toolCall.Name) && !string.IsNullOrEmpty(evt.ToolName))
            {
                toolCall.Name = evt.ToolName;
            }

            // 根据状态更新显示
            toolCall.Status = MapToolCallStatus(evt.Status);

            if (!string.IsNullOrEmpty(evt.Output))
            {
                toolCall.Result = evt.Output;
            }

            if (!string.IsNullOrEmpty(evt.Error))
            {
                toolCall.Error = evt.Error;
            }

            // task 工具：从参数/输出补齐 Task 卡片字段，避免完成后退化成普通工具调用
            EnrichTaskToolCall(toolCall);

            // 处理 todowrite 工具：提取 Todo 列表并更新 SessionState
            if (evt.ToolName?.ToLowerInvariant() == "todowrite" &&
                evt.Status == ToolCallStatus.Success &&
                !string.IsNullOrEmpty(evt.Output))
            {
                UpdateTodoList(evt.SessionId, evt.Output);
            }
        }

        /// <summary>
        /// 从参数/结果补齐 Task 子代理卡片字段，并写入索引
        /// </summary>
        private void EnrichTaskToolCall(SessionToolCall toolCall)
        {
            if (toolCall == null)
                return;

            var isTask = string.Equals(toolCall.Name, "task", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(toolCall.TaskId)
                || (!string.IsNullOrEmpty(toolCall.Result) &&
                    toolCall.Result.Contains("task_id:", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(toolCall.Arguments) &&
                    toolCall.Arguments.Contains("subagent_type", StringComparison.OrdinalIgnoreCase));

            if (!isTask)
                return;

            if (string.IsNullOrEmpty(toolCall.Name))
                toolCall.Name = "task";

            TryFillTaskFieldsFromArguments(toolCall);
            TryFillTaskIdFromResult(toolCall);

            if (!string.IsNullOrEmpty(toolCall.TaskId))
                _taskIndex[toolCall.TaskId] = toolCall;
        }

        private static void TryFillTaskFieldsFromArguments(SessionToolCall toolCall)
        {
            if (string.IsNullOrWhiteSpace(toolCall.Arguments))
                return;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(toolCall.Arguments);
                var root = doc.RootElement;
                if (string.IsNullOrEmpty(toolCall.TaskDescription) &&
                    root.TryGetProperty("description", out var desc) &&
                    desc.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    toolCall.TaskDescription = desc.GetString();
                }

                if (string.IsNullOrEmpty(toolCall.TaskAgent) &&
                    root.TryGetProperty("subagent_type", out var agent) &&
                    agent.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    toolCall.TaskAgent = agent.GetString();
                }

                if (root.TryGetProperty("background", out var bg) &&
                    bg.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    toolCall.TaskBackground = bg.GetBoolean();
                }
                else if (root.TryGetProperty("run_in_background", out var bg2) &&
                         bg2.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    toolCall.TaskBackground = bg2.GetBoolean();
                }
            }
            catch
            {
                // ignore malformed args
            }
        }

        private static void TryFillTaskIdFromResult(SessionToolCall toolCall)
        {
            if (!string.IsNullOrEmpty(toolCall.TaskId) || string.IsNullOrWhiteSpace(toolCall.Result))
                return;

            const string prefix = "task_id:";
            var idx = toolCall.Result.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return;

            var start = idx + prefix.Length;
            while (start < toolCall.Result.Length && char.IsWhiteSpace(toolCall.Result[start]))
                start++;

            var end = start;
            while (end < toolCall.Result.Length && !char.IsWhiteSpace(toolCall.Result[end]))
                end++;

            if (end > start)
                toolCall.TaskId = toolCall.Result[start..end];
        }

        /// <summary>
        /// 从 todowrite 工具输出更新 Todo 列表
        /// </summary>
        private void UpdateTodoList(string sessionId, string output)
        {
            try
            {
                var todoList = TodoListViewModel.FromJson(sessionId, output);
                todoList.LastUpdated = DateTime.Now;
                _sessionState.UpdateTodoList(todoList);
            }
            catch
            {
                // 解析失败，忽略
            }
        }

        /// <summary>
        /// 处理子代理事件（已废弃，保留兼容）
        /// </summary>
#pragma warning disable CS0618
        private void HandleSubAgent(SubAgentEvent evt)
#pragma warning restore CS0618
        {
            // 已由 HandleTask* 替代
        }

        private void HandleTaskStarted(TaskStartedEvent evt)
        {
            // 服务端 TaskSessionProjector 已写入字段；UI 只刷新卡片索引
            var toolCall = ResolveTaskToolCall(evt.OriginToolCallId, evt.TaskId);
            if (toolCall != null)
                _taskIndex[evt.TaskId] = toolCall;
        }

        private void HandleTaskProgress(TaskProgressEvent evt)
        {
            var toolCall = ResolveTaskToolCall(evt.OriginToolCallId, evt.TaskId);
            if (toolCall != null)
                _taskIndex[evt.TaskId] = toolCall;
        }

        private void HandleTaskCompleted(TaskCompletedEvent evt)
        {
            var toolCall = ResolveTaskToolCall(evt.OriginToolCallId, evt.TaskId);
            if (toolCall != null)
                _taskIndex[evt.TaskId] = toolCall;
        }

        private void HandleTaskFailed(TaskFailedEvent evt)
        {
            var toolCall = ResolveTaskToolCall(evt.OriginToolCallId, evt.TaskId);
            if (toolCall != null)
                _taskIndex[evt.TaskId] = toolCall;
        }

        private SessionToolCall? ResolveTaskToolCall(string? originToolCallId, string taskId)
        {
            if (!string.IsNullOrEmpty(originToolCallId) &&
                _currentAssistantMessage?.ToolCalls != null)
            {
                var byOrigin = _currentAssistantMessage.ToolCalls.Find(t => t.Id == originToolCallId);
                if (byOrigin != null)
                    return byOrigin;
            }

            if (_taskIndex.TryGetValue(taskId, out var indexed))
                return indexed;

            return FindTaskToolCallInSession(taskId);
        }

        private SessionToolCall? FindTaskToolCallInSession(string taskId)
        {
            var messages = _sessionState.CurrentSession?.Messages;
            if (messages == null)
                return null;

            foreach (var msg in messages)
            {
                var match = msg.ToolCalls?.FirstOrDefault(t => t.TaskId == taskId);
                if (match != null)
                    return match;
            }

            return null;
        }

        /// <summary>
        /// 处理权限请求事件
        /// </summary>
        private void HandlePermissionRequest(PermissionRequestEvent evt)
        {
            // 添加权限请求到待处理队列
            _pendingPermissions[evt.PermissionId] = new PermissionRequestViewModel
            {
                PermissionId = evt.PermissionId,
                PermissionKind = evt.PermissionKind,
                Resource = evt.Resource,
                Arguments = evt.Arguments,
                RiskLevel = evt.RiskLevel,
                Message = evt.Message,
                TimeoutSeconds = evt.TimeoutSeconds,
                Timestamp = evt.Timestamp
            };

            // 触发权限 UI 显示
            OnPermissionRequest?.Invoke(evt);
        }

        /// <summary>
        /// 处理权限响应事件
        /// </summary>
        private void HandlePermissionResponse(PermissionResponseEvent evt)
        {
            // 移除待处理权限
            _pendingPermissions.Remove(evt.PermissionId);

            // 触发权限 UI 更新
            OnPermissionResponse?.Invoke(evt);
        }

        /// <summary>
        /// 处理 Loop 取消事件
        /// </summary>
        private void HandleLoopCancelled(LoopCancelledEvent evt)
        {
            if (_currentLoop != null)
            {
                _currentLoop.EndTime = evt.Timestamp;
                _currentLoop.IsComplete = true;
                _currentLoop.Success = false;
                _currentLoop.Error = $"已取消: {evt.Reason}";

                OnLoopComplete?.Invoke(_currentLoop);
            }

            // 取消/错误消息由服务端 ChatEventTracker 写入 Session
        }

        private void HandleError(ErrorEvent evt)
        {
            // 展示用：绑定到服务端已写入的消息；不在此追加以免重复落盘内容
            EnsureCurrentAssistantMessage(evt.SessionId);
            _ = FormatErrorDetails(evt);
        }

        /// <summary>
        /// 处理 Skill 内容展开事件
        /// </summary>
        private void HandleSkillContent(SkillContentEvent evt)
        {
            // 更新当前用户消息的内容为展开后的 Skill 内容
            // ChatOrchestrator 已经更新了 Session 中的消息，这里只需要触发 UI 刷新
            // OnStateChanged 会在 ProcessEventAsync 末尾调用
        }

        /// <summary>
        /// 处理 Budget 状态事件
        /// </summary>
        private void HandleBudgetStatus(BudgetStatusEvent evt)
        {
            var status = new BudgetStatusResponse
            {
                CurrentTokens = evt.CurrentTokens,
                MaxTokens = evt.MaxTokens,
                AvailableTokens = evt.MaxTokens - evt.CurrentTokens,
                UsagePercentage = evt.UsagePercentage,
                Level = evt.Level.ToString().ToLowerInvariant(),
                Message = $"Token usage: {evt.CurrentTokens}/{evt.MaxTokens}",
                NeedsCompaction = evt.Level == BudgetLevel.Critical || evt.Level == BudgetLevel.Overflow
            };
            _sessionState.UpdateBudgetStatus(status);
        }

        /// <summary>
        /// 处理 Budget 警告事件
        /// </summary>
        private void HandleBudgetWarning(BudgetWarningEvent evt)
        {
            // 警告事件可以显示通知，但不需要更新状态（状态由 BudgetStatusEvent 更新）
            // OnStateChanged 会触发 UI 刷新
        }

        /// <summary>
        /// 处理压缩事件
        /// </summary>
        private void HandleCompaction(CompactionEvent evt)
        {
            // 压缩完成，状态已由 BudgetStatusEvent 更新
            // OnStateChanged 会触发 UI 刷新
        }

        /// <summary>
        /// 处理 Todo 更新事件 - 从 ACP AgentPlanUpdate 映射
        /// </summary>
        private void HandleTodoUpdate(TodoUpdateEvent evt)
        {
            var todoList = new TodoListViewModel
            {
                SessionId = evt.SessionId,
                LastUpdated = evt.Timestamp
            };

            foreach (var item in evt.Todos)
            {
                todoList.Items.Add(new TodoItemViewModel
                {
                    Id = Guid.NewGuid().ToString()[..8],
                    Content = item.Content,
                    Status = MapTodoStatus(item.Status),
                    Priority = MapTodoPriority(item.Priority)
                });
            }

            _sessionState.UpdateTodoList(todoList);
        }

        /// <summary>
        /// 处理模式更新事件 - 从 ACP CurrentModeUpdate 映射
        /// </summary>
        private void HandleModeUpdate(ModeUpdateEvent evt)
        {
            // 更新 Session 的 ACP Mode
            _sessionState.SelectedAcpMode = evt.ModeId;
        }

        /// <summary>
        /// 映射 Todo 状态
        /// </summary>
        private static TodoStatusViewModel MapTodoStatus(TodoStatus status) => status switch
        {
            TodoStatus.Pending => TodoStatusViewModel.Pending,
            TodoStatus.InProgress => TodoStatusViewModel.InProgress,
            TodoStatus.Completed => TodoStatusViewModel.Completed,
            TodoStatus.Cancelled => TodoStatusViewModel.Cancelled,
            _ => TodoStatusViewModel.Pending
        };

        /// <summary>
        /// 映射 Todo 优先级
        /// </summary>
        private static TodoPriorityViewModel MapTodoPriority(TodoPriority priority) => priority switch
        {
            TodoPriority.Low => TodoPriorityViewModel.Low,
            TodoPriority.Medium => TodoPriorityViewModel.Medium,
            TodoPriority.High => TodoPriorityViewModel.High,
            _ => TodoPriorityViewModel.Medium
        };

        /// <summary>
        /// 格式化错误详情为用户友好的消息
        /// </summary>
        private static string FormatErrorDetails(ErrorEvent evt)
        {
            var message = evt.Message;

            // 根据异常类型提供更友好的消息
            if (evt.Exception is Llm.LlmRetryExhaustedException retryEx)
            {
                return $"请求在 {retryEx.MaxRetries} 次重试后仍然失败。请稍后重试或检查网络连接。";
            }

            if (evt.Exception is Llm.LlmTimeoutException timeoutEx)
            {
                return $"请求超时 ({timeoutEx.Timeout.TotalSeconds:F1}秒)。请稍后重试。";
            }

            if (evt.Exception is Llm.LlmStreamingException streamEx)
            {
                return $"流式响应中断: {streamEx.Message}。请重新发起请求。";
            }

            if (evt.Exception is Llm.LlmConnectionException)
            {
                return "网络连接错误。请检查网络连接后重试。";
            }

            if (evt.Exception is Llm.LlmException llmEx)
            {
                return $"LLM 服务错误: {llmEx.Message}";
            }

            if (evt.Exception is OperationCanceledException)
            {
                return "请求已被取消。";
            }

            if (evt.Exception is IOException ioEx)
            {
                return $"网络连接错误: {ioEx.Message}。请检查网络后重试。";
            }

            // 默认返回原始消息
            return message;
        }

        /// <summary>
        /// 将 Core 层 ToolCallStatus 映射为 WebUI 显示状态
        /// </summary>
        private static string MapToolCallStatus(ToolCallStatus status) => status switch
        {
            ToolCallStatus.Pending => "pending",
            ToolCallStatus.Running => "running",
            ToolCallStatus.Success => "success",
            ToolCallStatus.Failed => "failed",
            ToolCallStatus.Rejected => "rejected",
            _ => "unknown"
        };

        /// <summary>
        /// 格式化 Arguments 为 JSON 字符串（确保格式正确）
        /// </summary>
        private static string FormatArgumentsJson(object? arguments)
        {
            if (arguments == null) return "{}";

            // 如果已经是 JSON 字符串，直接返回
            if (arguments is string str)
            {
                // 验证是否为有效 JSON
                try
                {
                    System.Text.Json.JsonSerializer.Deserialize<object>(str);
                    return str;
                }
                catch
                {
                    // 不是有效 JSON，包装为 JSON 字符串值
                    return System.Text.Json.JsonSerializer.Serialize(str);
                }
            }

            // 其他类型（字典等），序列化为 JSON
            try
            {
                return System.Text.Json.JsonSerializer.Serialize(arguments);
            }
            catch
            {
                return "{}";
            }
        }

        /// <summary>
        /// 确保有当前助手消息（用于流式增量）
        /// </summary>
        private void EnsureCurrentAssistantMessage(string sessionId)
        {
            if (_currentAssistantMessage != null) return;

            // 服务端 ChatEventTracker 使用相同 ID 规则写入；UI 只绑定，不创建
            var loopPrefix = !string.IsNullOrEmpty(_currentLoopId) ? _currentLoopId : sessionId;
            var messageId = $"{loopPrefix}_step{_currentStep}";

            var existing = _sessionState.CurrentSession?.Messages?
                .LastOrDefault(m => m.Id == messageId && m.Role == "assistant");
            if (existing == null)
            {
                existing = _sessionState.CurrentSession?.Messages?
                    .LastOrDefault(m => m.Role == "assistant"
                        && (string.IsNullOrEmpty(_currentLoopId) || m.LoopId == _currentLoopId));
            }

            if (existing != null)
            {
                _currentAssistantMessage = existing;
                if (!string.IsNullOrEmpty(_currentLoopId))
                    _currentAssistantMessage.LoopId = _currentLoopId;
            }
        }

        /// <summary>
        /// 清空当前助手消息（新会话开始时）
        /// </summary>
        public void ClearCache()
        {
            _currentAssistantMessage = null;
            _currentLoopId = null;
            _currentLoop = null;
            _currentStep = 0;
            _toolCallPositions.Clear();
            _accumulatedReasoning.Clear();
            _accumulatedContent.Clear();
            _taskIndex.Clear();
        }

        /// <summary>
        /// 同步 UI 上未完成 Task 的展示状态。落盘由 ExecutionJobService 在取消时完成。
        /// </summary>
        public Task<int> MarkIncompleteTasksCancelledAsync(string reason = "用户取消", bool persist = false)
        {
            EnsureLiveSessionSynced();
            var count = 0;

            void Mark(SessionToolCall tc)
            {
                if (!IsIncompleteTaskToolCall(tc))
                    return;

                tc.Status = "cancelled";
                tc.Error = reason;
                count++;
                if (!string.IsNullOrEmpty(tc.TaskId))
                    _taskIndex[tc.TaskId] = tc;
            }

            foreach (var tc in _taskIndex.Values.ToList())
                Mark(tc);

            if (_currentAssistantMessage?.ToolCalls != null)
            {
                foreach (var tc in _currentAssistantMessage.ToolCalls)
                    Mark(tc);
            }

            if (_sessionState.CurrentSession?.Messages != null)
            {
                foreach (var msg in _sessionState.CurrentSession.Messages)
                {
                    if (msg.ToolCalls == null)
                        continue;
                    foreach (var tc in msg.ToolCalls)
                        Mark(tc);
                }
            }

            // persist 参数保留兼容；执行轨迹落盘一律由服务端负责
            _ = persist;
            OnStateChanged?.Invoke();
            return Task.FromResult(count);
        }

        /// <summary>
        /// 加载会话后：若 Task 仍为 running/pending 且后台 Job 已不存在，标记为已取消
        /// </summary>
        public async Task<int> ReconcileOrphanTaskCardsAsync(
            Func<string, Task<bool>> isTaskStillActiveAsync,
            string reason = "任务已中断（进程关闭或取消）")
        {
            if (_sessionState.CurrentSession?.Messages == null)
                return 0;

            var count = 0;
            foreach (var msg in _sessionState.CurrentSession.Messages)
            {
                if (msg.ToolCalls == null)
                    continue;

                foreach (var tc in msg.ToolCalls)
                {
                    if (!IsIncompleteTaskToolCall(tc))
                        continue;

                    var taskId = tc.TaskId;
                    if (string.IsNullOrEmpty(taskId))
                    {
                        tc.Status = "cancelled";
                        tc.Error = reason;
                        count++;
                        continue;
                    }

                    var stillActive = false;
                    try
                    {
                        stillActive = await isTaskStillActiveAsync(taskId);
                    }
                    catch
                    {
                        stillActive = false;
                    }

                    if (stillActive)
                        continue;

                    tc.Status = "cancelled";
                    tc.Error = reason;
                    _taskIndex[taskId] = tc;
                    count++;
                }
            }

            if (count > 0 && _sessionState.CurrentSession != null)
            {
                // 加载期孤儿修复：需写回存储（非执行流路径）
                await _sessionManager.SaveAsync(_sessionState.CurrentSession.Id);
            }

            return count;
        }

        private static bool IsIncompleteTaskToolCall(SessionToolCall tc)
        {
            if (tc == null)
                return false;

            var isTask = !string.IsNullOrEmpty(tc.TaskId)
                || string.Equals(tc.Name, "task", StringComparison.OrdinalIgnoreCase);

            if (!isTask)
                return false;

            var status = tc.Status?.ToLowerInvariant();
            return status is "running" or "pending";
        }

        /// <summary>
        /// 获取当前助手消息的内容（用于 UI 显示）
        /// </summary>
        public string GetCurrentAssistantContent()
        {
            // 优先返回累积内容（跨多轮），否则返回当前消息内容
            return _accumulatedContent.Length > 0
                ? _accumulatedContent.ToString()
                : _currentAssistantMessage?.Content ?? "";
        }

        /// <summary>
        /// 获取当前助手消息的推理内容（用于 UI 显示）
        /// </summary>
        public string? GetCurrentAssistantReasoning()
        {
            // 优先返回累积推理内容（跨多轮），否则返回当前消息内容
            return _accumulatedReasoning.Length > 0
                ? _accumulatedReasoning.ToString()
                : _currentAssistantMessage?.ReasoningContent;
        }

        /// <summary>
        /// 获取当前助手消息的工具调用（用于 UI 显示）
        /// </summary>
        public List<SessionToolCall>? GetCurrentAssistantToolCalls()
        {
            return _currentAssistantMessage?.ToolCalls;
        }

        /// <summary>
        /// 获取工具调用的内容位置（UI 层自行管理）
        /// </summary>
        public Dictionary<string, int> GetToolCallPositions()
        {
            return _toolCallPositions;
        }

        /// <summary>
        /// 获取当前助手消息（完整对象）
        /// </summary>
        public SessionMessage? GetCurrentAssistantMessage()
        {
            return _currentAssistantMessage;
        }

        /// <summary>
        /// 同步处理事件（向后兼容）——仍应优先 await ProcessEventAsync
        /// </summary>
        public void ProcessEvent(IMessageEvent evt)
        {
            ProcessEventAsync(evt).GetAwaiter().GetResult();
        }
    }
}