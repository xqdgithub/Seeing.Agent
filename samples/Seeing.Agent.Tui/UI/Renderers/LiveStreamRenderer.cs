using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Models;
using Seeing.Agent.Core.Events;

namespace Seeing.Agent.Tui.UI.Renderers;

/// <summary>
/// 流式消息渲染器 - 简化版本
/// </summary>
public sealed class LiveStreamRenderer
{
    private readonly TuiState _state;
    private readonly List<ToolCallDisplay> _toolCalls = new();

    public LiveStreamRenderer(TuiState state)
    {
        _state = state;
    }

    public Task HandleDeltaAsync(StreamDeltaEvent evt)
    {
        // 确保流式消息存在
        _state.CurrentStreamingMessage ??= new StreamingMessage();

        // 累积内容
        if (!string.IsNullOrEmpty(evt.ReasoningDelta))
        {
            _state.CurrentStreamingMessage.AppendReasoning(evt.ReasoningDelta);
        }

        if (!string.IsNullOrEmpty(evt.ContentDelta))
        {
            _state.CurrentStreamingMessage.AppendContent(evt.ContentDelta);
        }

        return Task.CompletedTask;
    }

    public Task HandleCompleteAsync(StreamCompleteEvent evt)
    {
        // 完成流式消息，添加到消息列表
        if (_state.CurrentStreamingMessage != null && _state.CurrentStreamingMessage.HasContent)
        {
            _state.AddMessage(new MessageDisplay
            {
                Role = "assistant",
                Content = _state.CurrentStreamingMessage.Content,
                Reasoning = _state.CurrentStreamingMessage.Reasoning,
                Timestamp = DateTime.Now,
                ToolCalls = _toolCalls.ToList(),
                IsComplete = true
            });
        }

        _state.CurrentStreamingMessage = null;
        _toolCalls.Clear();

        return Task.CompletedTask;
    }

    public Task HandleToolCallAsync(ToolCallEvent evt)
    {
        // 查找或创建工具调用显示
        var existing = _toolCalls.FirstOrDefault(t => t.Id == evt.ToolCallId);
        if (existing == null)
        {
            existing = new ToolCallDisplay
            {
                Id = evt.ToolCallId ?? "",
                Name = evt.ToolName ?? "unknown"
            };
            _toolCalls.Add(existing);
        }

        // 工具调用状态已在 ToolCallDisplay 构造时设置
        return Task.CompletedTask;
    }

    public Task HandleSubAgentAsync(SubAgentEvent evt)
    {
        // 暂不处理子代理事件
        return Task.CompletedTask;
    }

    public Task HandleErrorAsync(ErrorEvent evt)
    {
        // 添加错误消息
        _state.AddMessage(new MessageDisplay
        {
            Role = "system",
            Content = $"错误: {evt.Message}",
            Timestamp = DateTime.Now
        });

        return Task.CompletedTask;
    }
}