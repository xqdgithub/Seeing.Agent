using Seeing.Agent.Core.Events;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.WebUI.Services
{
    /// <summary>
    /// 权限请求事件 - WebUI 特有事件，用于 UI 侧权限交互
    /// </summary>
    public record PermissionRequestEvent : IMessageEvent
    {
        public required string SessionId { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public MessageEventType Type => MessageEventType.Error; // 兼容接口，实际使用 EventType 属性

        /// <summary>权限请求 ID</summary>
        public string PermissionId { get; init; } = "";

        /// <summary>权限类型/工具名</summary>
        public string Permission { get; init; } = "";

        /// <summary>资源路径</summary>
        public string? Resource { get; init; }

        /// <summary>请求消息</summary>
        public string? Message { get; init; }

        /// <summary>风险等级</summary>
        public string? RiskLevel { get; init; }
    }

    /// <summary>
    /// 事件流处理器 - 处理 Core 层消息事件并更新 SessionState
    /// <para>
    /// 已重构：使用 Core 层统一事件类型（IMessageEvent），支持工具状态流转
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
        /// 工具调用的内容位置（UI 层自行管理）
        /// Key: ToolCallId, Value: 工具调用首次出现时的内容长度
        /// </summary>
        private readonly Dictionary<string, int> _toolCallPositions = new();

        /// <summary>
        /// UI 更新回调
        /// </summary>
        public event Action? OnStateChanged;

        public EventStreamHandler(SessionState sessionState, SessionManager sessionManager)
        {
            _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        }

        /// <summary>
        /// 处理 Core 层消息事件
        /// </summary>
        public async Task ProcessEventAsync(IMessageEvent evt)
        {
            // 特殊处理 WebUI 特有事件
            if (evt is PermissionRequestEvent permEvent)
            {
                HandlePermissionRequest(permEvent);
                OnStateChanged?.Invoke();
                return;
            }

            switch (evt.Type)
            {
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

                case MessageEventType.Error:
                    HandleError((ErrorEvent)evt);
                    await SaveToSessionAsync();
                    break;
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 处理流式开始事件 - 新轮次开始，清空状态
        /// </summary>
        private void HandleStreamStart(StreamStartEvent evt)
        {
            // 新轮次开始，清空当前助手消息，准备接收新一轮的 delta
            // 注意：step > 0 时表示这是工具调用后的后续轮次
            if (evt.Step > 0 || _currentAssistantMessage == null)
            {
                _currentAssistantMessage = null;
                _toolCallPositions.Clear();
            }
        }

        private void HandleStreamDelta(StreamDeltaEvent evt)
        {
            // 确保有当前助手消息
            EnsureCurrentAssistantMessage(evt.SessionId);

            if (_currentAssistantMessage != null)
            {
                if (!string.IsNullOrEmpty(evt.ContentDelta))
                {
                    _currentAssistantMessage.Content += evt.ContentDelta;
                }
                if (!string.IsNullOrEmpty(evt.ReasoningDelta))
                {
                    _currentAssistantMessage.ReasoningContent =
                        (_currentAssistantMessage.ReasoningContent ?? string.Empty) + evt.ReasoningDelta;
                }
            }
        }

        private void HandleStreamComplete(StreamCompleteEvent evt)
        {
            if (_currentAssistantMessage != null && evt.Message != null)
            {
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
            if (_currentAssistantMessage == null) return;

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
        /// 处理权限请求事件（WebUI 特有）
        /// </summary>
        private void HandlePermissionRequest(PermissionRequestEvent evt)
        {
            // 权限请求通过 BlazorPermissionChannel 的 RespondToPermission 方法处理
            // 这里仅触发 UI 状态更新，让 UI 显示权限请求界面
            // 实际实现由 UI 组件（如 PermissionDialog）完成
        }

        private void HandleError(ErrorEvent evt)
        {
            if (_currentAssistantMessage != null)
            {
                _currentAssistantMessage.Content += $"\n\n⚠️ 错误: {evt.Message}";
            }

            // 添加系统错误消息
            if (_sessionState.CurrentSession != null)
            {
                _sessionState.CurrentSession.AddMessage(SessionMessage.SystemMessage($"⚠️ 错误: {evt.Message}"));
            }
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

            // 创建新的助手消息
            _currentAssistantMessage = SessionMessage.AssistantMessage("");
            _currentAssistantMessage.Id = sessionId;

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
            _toolCallPositions.Clear();
        }

        /// <summary>
        /// 获取当前助手消息的内容（用于 UI 显示）
        /// </summary>
        public string GetCurrentAssistantContent()
        {
            return _currentAssistantMessage?.Content ?? "";
        }

        /// <summary>
        /// 获取当前助手消息的推理内容（用于 UI 显示）
        /// </summary>
        public string? GetCurrentAssistantReasoning()
        {
            return _currentAssistantMessage?.ReasoningContent;
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
        /// 同步处理事件（向后兼容）
        /// </summary>
        public void ProcessEvent(IMessageEvent evt)
        {
            ProcessEventAsync(evt).ConfigureAwait(false);
        }
    }
}