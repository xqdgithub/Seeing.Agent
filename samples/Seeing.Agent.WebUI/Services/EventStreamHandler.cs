using Seeing.Agent.Core.Events;
using Seeing.Agent.App.Events;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Agent.TokenBudget.Api.Responses;
using Seeing.Agent.TokenBudget;
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
        private readonly SessionManager _sessionManager;

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

        public EventStreamHandler(SessionState sessionState, SessionManager sessionManager)
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
        /// 处理 Core 层消息事件
        /// </summary>
        public async Task ProcessEventAsync(IMessageEvent evt)
        {
            switch (evt.Type)
            {
                case MessageEventType.LoopStart:
                    HandleLoopStart((LoopStartEvent)evt);
                    break;

                case MessageEventType.LoopComplete:
                    HandleLoopComplete((LoopCompleteEvent)evt);
                    break;

                case MessageEventType.StreamStart:
                    HandleStreamStart((StreamStartEvent)evt);
                    break;

                case MessageEventType.StreamDelta:
                    HandleStreamDelta((StreamDeltaEvent)evt);
                    break;

                case MessageEventType.StreamComplete:
                    HandleStreamComplete((StreamCompleteEvent)evt);
                    await SaveToSessionAsync();
                    break;

                case MessageEventType.ToolCallPending:
                case MessageEventType.ToolCallRunning:
                case MessageEventType.ToolCallComplete:
                    HandleToolCall((ToolCallEvent)evt);
                    break;

                case MessageEventType.SubAgentStarted:
                case MessageEventType.SubAgentCompleted:
                    HandleSubAgent((SubAgentEvent)evt);
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
                    await SaveToSessionAsync();
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

                default:
                    // 未处理的事件类型，记录日志（用于调试）
                    // _logger.LogDebug("未处理事件类型: {EventType}", evt.Type);
                    break;
            }

            OnStateChanged?.Invoke();
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
            // 更新当前 LoopId（如果事件中有）
            if (!string.IsNullOrEmpty(evt.LoopId))
            {
                _currentLoopId = evt.LoopId;
            }

            // 确保有当前助手消息
            EnsureCurrentAssistantMessage(evt.SessionId);

            if (_currentAssistantMessage != null)
            {
                // 设置 LoopId
                if (!string.IsNullOrEmpty(evt.LoopId))
                {
                    _currentAssistantMessage.LoopId = evt.LoopId;
                }

                if (!string.IsNullOrEmpty(evt.ContentDelta))
                {
                    _currentAssistantMessage.Content += evt.ContentDelta;
                    // 同时累积到 Loop 级别
                    _accumulatedContent.Append(evt.ContentDelta);
                }
                if (!string.IsNullOrEmpty(evt.ReasoningDelta))
                {
                    _currentAssistantMessage.ReasoningContent =
                        (_currentAssistantMessage.ReasoningContent ?? string.Empty) + evt.ReasoningDelta;
                    // 同时累积到 Loop 级别（支持多轮思考）
                    _accumulatedReasoning.Append(evt.ReasoningDelta);
                }
            }
        }

        private void HandleStreamComplete(StreamCompleteEvent evt)
        {
            if (_currentAssistantMessage != null && evt.Message != null)
            {
                // 设置 LoopId
                if (!string.IsNullOrEmpty(evt.LoopId))
                {
                    _currentAssistantMessage.LoopId = evt.LoopId;
                }

                // 更新完整消息内容（如果流式内容不完整）
                if (!string.IsNullOrEmpty(evt.Message.Content) &&
                    string.IsNullOrEmpty(_currentAssistantMessage.Content))
                {
                    _currentAssistantMessage.Content = evt.Message.Content;
                }
                if (!string.IsNullOrEmpty(evt.Message.ReasoningContent) &&
                    string.IsNullOrEmpty(_currentAssistantMessage.ReasoningContent))
                {
                    _currentAssistantMessage.ReasoningContent = evt.Message.ReasoningContent;
                }
            }
            // 注意：不在此处调用 CompleteExecution()
            // 因为工具调用后可能还有后续轮次，整个 Agent 执行由 Session.razor 控制
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
                    Name = evt.ToolName,
                    Arguments = FormatArgumentsJson(evt.Arguments)
                };
                _currentAssistantMessage.ToolCalls.Add(toolCall);

                // 记录该工具调用的内容位置（用于渲染）
                _toolCallPositions[toolCallId] = contentPosition;
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

            // 处理 todowrite 工具：提取 Todo 列表并更新 SessionState
            if (evt.ToolName?.ToLowerInvariant() == "todowrite" &&
                evt.Status == ToolCallStatus.Success &&
                !string.IsNullOrEmpty(evt.Output))
            {
                UpdateTodoList(evt.SessionId, evt.Output);
            }
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
        /// 处理子代理事件
        /// </summary>
        private void HandleSubAgent(SubAgentEvent evt)
        {
            // 可扩展：添加子代理状态显示
            // 当前简化处理，仅记录日志
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

            // 添加取消标记消息
            if (_sessionState.CurrentSession != null)
            {
                _sessionState.CurrentSession.AddMessage(
                    SessionMessage.SystemMessage($"⚠️ 对话已取消: {evt.Reason}"));
            }
        }

        private void HandleError(ErrorEvent evt)
        {
            var errorDetails = FormatErrorDetails(evt);

            if (_currentAssistantMessage != null)
            {
                _currentAssistantMessage.Content += $"\n\n⚠️ 错误: {errorDetails}";
            }

            // 添加系统错误消息
            if (_sessionState.CurrentSession != null)
            {
                var errorMessage = SessionMessage.SystemMessage($"⚠️ 错误: {errorDetails}")
                    .WithMetadata("errorSource", evt.Source ?? "unknown")
                    .WithMetadata("errorType", evt.Exception?.GetType().Name ?? "Unknown");

                if (evt.Exception is Llm.LlmException llmEx)
                {
                    errorMessage
                        .WithMetadata("modelId", llmEx.ModelId ?? "")
                        .WithMetadata("providerId", llmEx.ProviderId ?? "")
                        .WithMetadata("isRetryable", llmEx.IsRetryable)
                        .WithMetadata("retryCount", llmEx.RetryCount);
                }

                _sessionState.CurrentSession.AddMessage(errorMessage);
            }
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

            // 创建新的助手消息，每个 step 使用独立的 ID
            _currentAssistantMessage = SessionMessage.AssistantMessage("");

            // 使用 loopId + step 生成唯一 ID，确保每个 Loop 的每个 step 都是独立的消息
            // loopId 每次对话都是唯一的，step 在 Loop 内递增
            var loopPrefix = _currentLoopId ?? sessionId;
            _currentAssistantMessage.Id = $"{loopPrefix}_step{_currentStep}";
            _currentAssistantMessage.Step = _currentStep;

            // 设置 LoopId
            if (!string.IsNullOrEmpty(_currentLoopId))
            {
                _currentAssistantMessage.LoopId = _currentLoopId;
            }

            // 添加到 SessionData
            if (_sessionState.CurrentSession != null)
            {
                _sessionState.CurrentSession.AddMessage(_currentAssistantMessage);
            }
        }

        /// <summary>
        /// 将当前助手消息保存到存储
        /// </summary>
        private async Task SaveToSessionAsync()
        {
            if (_sessionState.CurrentSession == null) return;

            await _sessionManager.SaveAsync(_sessionState.CurrentSession.Id);
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
        /// 同步处理事件（向后兼容）
        /// </summary>
        public void ProcessEvent(IMessageEvent evt)
        {
            ProcessEventAsync(evt).ConfigureAwait(false);
        }
    }
}