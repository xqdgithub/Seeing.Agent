using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Models;
using Seeing.Agent.Tui.Services;
using Seeing.Agent.Tui.UI;

// 解决 TuiState 命名冲突（Core.TuiState vs State.TuiState）
using TuiState = Seeing.Agent.Tui.Core.State.TuiState;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 事件路由器 - 在 Live 上下文中消费 Channel 并刷新 UI
/// </summary>
/// <remarks>
/// 设计目标：
/// 1. 单线程渲染：在 Live.StartAsync 上下文中唯一消费者
/// 2. 批量刷新：每 100ms 或每 10 事件刷新一次，避免高频刷新
/// 3. 事件路由：根据事件类型分发到对应 UI 组件
/// 4. 状态更新：直接更新 TuiState，触发 UI 重绘
/// 5. 取消支持：响应 CancellationToken，优雅退出
/// </remarks>
public sealed class EventRouter
{
    /// <summary>刷新间隔（毫秒）</summary>
    public const int RefreshIntervalMs = 100;

    /// <summary>批量刷新阈值（事件数）</summary>
    public const int BatchRefreshThreshold = 10;

    private readonly EventChannelService _channel;
    private readonly TuiState _state;
    private readonly RenderService _renderService;
    private readonly MessagePanel _messagePanel;
    private readonly HashSet<string> _expandedToolIds = new();
    private readonly HashSet<string> _expandedReasoningIds = new();
    private readonly Dictionary<string, ToolCallDisplay> _activeToolCalls = new();
    private readonly Dictionary<string, SubAgentDisplay> _activeSubAgents = new();

    /// <summary>是否正在运行</summary>
    public bool IsRunning { get; private set; }

    /// <summary>已处理事件计数</summary>
    public int ProcessedCount { get; private set; }

    /// <summary>
    /// 构造事件路由器
    /// </summary>
    /// <param name="channel">事件通道服务</param>
    /// <param name="state">TUI 状态</param>
    /// <param name="renderService">渲染服务</param>
    public EventRouter(
        EventChannelService channel,
        TuiState state,
        RenderService renderService)
    {
        _channel = channel;
        _state = state;
        _renderService = renderService;
        _messagePanel = new MessagePanel(state, renderService);
    }

    /// <summary>
    /// 在 Live 上下文中启动事件路由
    /// </summary>
    /// <param name="ctx">Live 显示上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <remarks>
    /// 此方法在 AnsiConsole.Live(target).StartAsync(async ctx => ...) 中调用。
    /// 它会持续消费 Channel 中的事件，直到 Channel 完成或取消。
    /// </remarks>
    public async Task RunAsync(LiveDisplayContext ctx, CancellationToken cancellationToken)
    {
        IsRunning = true;
        ProcessedCount = 0;

        var lastRefreshTime = DateTime.UtcNow;
        var pendingRefreshCount = 0;

        try
        {
            // 循环消费 Channel
            while (!cancellationToken.IsCancellationRequested && await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                // 批量读取事件
                var batch = _channel.ReadBatch(BatchRefreshThreshold);

                // 处理每个事件
                foreach (var evt in batch)
                {
                    await RouteEventAsync(evt);
                    ProcessedCount++;
                    pendingRefreshCount++;
                }

                // 判断是否需要刷新
                var now = DateTime.UtcNow;
                var elapsedMs = (now - lastRefreshTime).TotalMilliseconds;
                var shouldRefresh = pendingRefreshCount >= BatchRefreshThreshold || elapsedMs >= RefreshIntervalMs;

                if (shouldRefresh && pendingRefreshCount > 0)
                {
                    // 在 Live 上下文中刷新 UI
                    ctx.Refresh();
                    lastRefreshTime = now;
                    pendingRefreshCount = 0;
                }
            }

            // 处理剩余事件
            while (_channel.Reader.TryRead(out var remainingEvent))
            {
                await RouteEventAsync(remainingEvent);
                ProcessedCount++;
            }

            // 最终刷新
            if (pendingRefreshCount > 0)
            {
                ctx.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，不处理
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// 路由事件到对应处理逻辑
    /// </summary>
    /// <param name="evt">事件对象</param>
    private async Task RouteEventAsync(IMessageEvent evt)
    {
        switch (evt.Type)
        {
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

            case MessageEventType.SubAgentStarted:
            case MessageEventType.SubAgentCompleted:
                HandleSubAgent((SubAgentEvent)evt);
                break;

            case MessageEventType.Error:
                HandleError((ErrorEvent)evt);
                break;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 处理流式增量事件 - 追加到 StreamingMessage
    /// </summary>
    /// <param name="evt">流式增量事件</param>
    private void HandleStreamDelta(StreamDeltaEvent evt)
    {
        // 确保流式消息存在
        _state.Messages.CurrentStreamingMessage ??= new StreamingMessage();

        // 累积内容
        if (!string.IsNullOrEmpty(evt.ReasoningDelta))
        {
            _state.Messages.CurrentStreamingMessage.AppendReasoning(evt.ReasoningDelta);
        }

        if (!string.IsNullOrEmpty(evt.ContentDelta))
        {
            _state.Messages.CurrentStreamingMessage.AppendContent(evt.ContentDelta);
        }
    }

    /// <summary>
    /// 处理流式完成事件 - 保存到消息列表
    /// </summary>
    /// <param name="evt">流式完成事件</param>
    private void HandleStreamComplete(StreamCompleteEvent evt)
    {
        // 获取流式内容
        var streaming = _state.Messages.CurrentStreamingMessage;

        // 添加完成的消息
        var msg = new MessageDisplay
        {
            Role = "assistant",
            Content = streaming?.Content ?? evt.Message.Content ?? "",
            Reasoning = streaming?.Reasoning ?? "",
            Timestamp = DateTime.Now,
            ToolCalls = _activeToolCalls.Values.ToList(),
            IsComplete = true
        };

        _state.AddMessage(msg);

        // 清空流式状态
        _state.Messages.ClearStreamingState();
        _activeToolCalls.Clear();
        _activeSubAgents.Clear();
    }

    /// <summary>
    /// 处理工具调用事件 - 更新 ToolCallDisplay 状态
    /// </summary>
    /// <param name="evt">工具调用事件</param>
    private void HandleToolCall(ToolCallEvent evt)
    {
        // 查找或创建工具调用显示
        if (!_activeToolCalls.TryGetValue(evt.ToolCallId, out var toolCall))
        {
            toolCall = new ToolCallDisplay
            {
                Id = evt.ToolCallId,
                Name = evt.ToolName,
                StartTime = evt.Timestamp,
                Arguments = evt.Arguments?.ToString() ?? ""
            };
            _activeToolCalls[evt.ToolCallId] = toolCall;
        }

        // 更新状态
        toolCall.Status = evt.Status;

        if (!string.IsNullOrEmpty(evt.Output))
        {
            toolCall.Result = evt.Output;
        }

        // Title 不存在于 ToolCallDisplay，忽略

        if (!string.IsNullOrEmpty(evt.Error))
        {
            toolCall.Error = evt.Error;
        }

        // Duration 是只读属性，由 EndTime 自动计算
        // 只设置 EndTime 即可
        if (evt.Duration.HasValue)
        {
            toolCall.EndTime = toolCall.StartTime + evt.Duration.Value;
        }
        else if (evt.Status == ToolCallStatus.Success || evt.Status == ToolCallStatus.Failed || evt.Status == ToolCallStatus.Rejected)
        {
            toolCall.EndTime = evt.Timestamp;
        }
    }

    /// <summary>
    /// 处理子代理事件 - 显示启动/完成状态
    /// </summary>
    /// <param name="evt">子代理事件</param>
    private void HandleSubAgent(SubAgentEvent evt)
    {
        // 查找或创建子代理显示
        var key = $"{evt.AgentName}_{evt.Timestamp.Ticks}";

        if (!_activeSubAgents.TryGetValue(evt.AgentName, out var subAgent))
        {
            subAgent = new SubAgentDisplay
            {
                Name = evt.AgentName,
                StartTime = evt.Timestamp,
                SubSessionId = evt.SubSessionId
            };
            _activeSubAgents[evt.AgentName] = subAgent;
        }

        // 更新状态
        subAgent.Status = evt.Status switch
        {
            "started" => SubAgentStatus.Running,
            "completed" => SubAgentStatus.Completed,
            "failed" => SubAgentStatus.Failed,
            _ => SubAgentStatus.Starting
        };

        if (!string.IsNullOrEmpty(evt.Result))
        {
            subAgent.Result = evt.Result;
        }

        if (!string.IsNullOrEmpty(evt.Error))
        {
            subAgent.Error = evt.Error;
        }

        if (subAgent.Status == SubAgentStatus.Completed || subAgent.Status == SubAgentStatus.Failed)
        {
            subAgent.EndTime = evt.Timestamp;
            // Duration 是只读属性，由 EndTime 自动计算
        }
    }

    /// <summary>
    /// 处理错误事件 - 显示错误面板
    /// </summary>
    /// <param name="evt">错误事件</param>
    private void HandleError(ErrorEvent evt)
    {
        // 添加错误消息
        var msg = new MessageDisplay
        {
            Role = "system",
            Content = $"错误: {evt.Message}",
            Timestamp = DateTime.Now,
            IsComplete = true
        };

        _state.AddMessage(msg);
    }

    /// <summary>
    /// 渲染当前 UI 状态（供 Live 显示使用）
    /// </summary>
    /// <returns>可渲染对象</returns>
    public IRenderable RenderCurrentState()
    {
        var rows = new List<IRenderable>();

        // 1. 渲染消息历史
        rows.Add(_messagePanel.Render());

        // 2. 渲染流式输出（如果有）
        if (_state.Messages.CurrentStreamingMessage?.HasContent == true)
        {
            rows.Add(RenderStreamingMessage());
        }

        // 3. 渲染活跃工具调用
        if (_activeToolCalls.Count > 0)
        {
            rows.Add(RenderActiveToolCalls());
        }

        // 4. 渲染活跃子代理
        if (_activeSubAgents.Count > 0)
        {
            rows.Add(RenderActiveSubAgents());
        }

        return new Rows(rows);
    }

    /// <summary>
    /// 渲染流式消息（实时显示）
    /// </summary>
    /// <returns>渲染结果</returns>
    private IRenderable RenderStreamingMessage()
    {
        var streaming = _state.Messages.CurrentStreamingMessage!;
        var rows = new List<IRenderable>();

        // 思考过程（如果有）
        if (streaming.HasReasoning)
        {
            var reasoningPanel = new ReasoningPanel(streaming.Reasoning);
            rows.Add(reasoningPanel.Render());
        }

        // 正文内容
        if (streaming.HasContentText)
        {
            var contentPanel = _renderService.RenderAssistantMessage(streaming.Content, true);
            rows.Add(contentPanel);
        }

        return new Rows(rows);
    }

    /// <summary>
    /// 渲染活跃工具调用列表
    /// </summary>
    /// <returns>渲染结果</returns>
    private IRenderable RenderActiveToolCalls()
    {
        var toolCalls = _activeToolCalls.Values.ToList();
        var rendered = ToolCallPanel.RenderMultiple(toolCalls, _expandedToolIds);
        return new Rows(rendered);
    }

    /// <summary>
    /// 渲染活跃子代理列表
    /// </summary>
    /// <returns>渲染结果</returns>
    private IRenderable RenderActiveSubAgents()
    {
        var subAgents = _activeSubAgents.Values.ToList();
        var rendered = SubAgentPanel.RenderMultiple(subAgents);
        return new Rows(rendered);
    }

    /// <summary>
    /// 切换工具调用展开状态
    /// </summary>
    /// <param name="toolCallId">工具调用 ID</param>
    public void ToggleToolCallExpand(string toolCallId)
    {
        if (_expandedToolIds.Contains(toolCallId))
        {
            _expandedToolIds.Remove(toolCallId);
        }
        else
        {
            _expandedToolIds.Add(toolCallId);
        }
    }

    /// <summary>
    /// 切换思考过程展开状态
    /// </summary>
    /// <param name="reasoningId">思考过程 ID</param>
    public void ToggleReasoningExpand(string reasoningId)
    {
        if (_expandedReasoningIds.Contains(reasoningId))
        {
            _expandedReasoningIds.Remove(reasoningId);
        }
        else
        {
            _expandedReasoningIds.Add(reasoningId);
        }
    }

    /// <summary>
    /// 清空所有活跃状态
    /// </summary>
    public void ClearActiveState()
    {
        _activeToolCalls.Clear();
        _activeSubAgents.Clear();
        _expandedToolIds.Clear();
        _expandedReasoningIds.Clear();
    }
}