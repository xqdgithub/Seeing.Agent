using System.Collections;
using Seeing.Agent.Tui.Core.Models;

namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// 消息存储 - 管理消息历史和流式输出状态（线程安全）
/// </summary>
public class MessageStore : IEnumerable<MessageDisplay>
{
    private readonly object _lock = new();
    private readonly List<MessageDisplay> _messages = new();
    private int _maxMessages = 100;

    /// <summary>最大显示消息数</summary>
    public int MaxMessages
    {
        get { lock (_lock) return _maxMessages; }
        set { lock (_lock) _maxMessages = value; }
    }

    /// <summary>正在流式输出的消息（实时渲染用）</summary>
    public StreamingMessage? CurrentStreamingMessage { get; set; }

    /// <summary>当前工具调用列表（实时渲染用）</summary>
    public List<ToolCallDisplay> CurrentToolCalls { get; } = new();

    /// <summary>状态变更回调</summary>
    public Action<RenderRegion>? OnStateChanged { get; set; }

    // ========== 列表般的访问接口 ==========

    /// <summary>消息数量</summary>
    public int Count { get { lock (_lock) return _messages.Count; } }

    /// <summary>获取指定索引的消息</summary>
    public MessageDisplay this[int index] { get { lock (_lock) return _messages[index]; } }

    /// <summary>获取内部消息列表副本（用于迭代）</summary>
    public List<MessageDisplay> GetSnapshot()
    {
        lock (_lock) return new List<MessageDisplay>(_messages);
    }

    // ========== 消息管理方法 ==========

    /// <summary>添加消息</summary>
    public void Add(MessageDisplay msg)
    {
        lock (_lock)
        {
            _messages.Add(msg);
            if (_messages.Count > _maxMessages)
                _messages.RemoveAt(0);
        }
        OnStateChanged?.Invoke(RenderRegion.Messages);
    }

    /// <summary>清空消息</summary>
    public void Clear()
    {
        lock (_lock) _messages.Clear();
        CurrentStreamingMessage = null;
        lock (CurrentToolCalls) CurrentToolCalls.Clear();
    }

    /// <summary>清空流式状态</summary>
    public void ClearStreamingState()
    {
        CurrentStreamingMessage = null;
        lock (CurrentToolCalls) CurrentToolCalls.Clear();
    }

    // ========== IEnumerable<MessageDisplay> 实现 ==========

    public IEnumerator<MessageDisplay> GetEnumerator() => GetSnapshot().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}