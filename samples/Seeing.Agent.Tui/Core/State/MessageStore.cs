using System.Collections;
using Seeing.Agent.Tui.Core.Models;

namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// 消息存储 - 管理消息历史和流式输出状态
/// </summary>
public class MessageStore : IEnumerable<MessageDisplay>
{
    /// <summary>消息列表</summary>
    private readonly List<MessageDisplay> _messages = new();

    /// <summary>最大显示消息数</summary>
    public int MaxMessages { get; set; } = 100;

    /// <summary>正在流式输出的消息（实时渲染用）</summary>
    public StreamingMessage? CurrentStreamingMessage { get; set; }

    /// <summary>当前工具调用列表（实时渲染用）</summary>
    public List<ToolCallDisplay> CurrentToolCalls { get; } = new();

    // ========== 列表般的访问接口 ==========

    /// <summary>消息数量</summary>
    public int Count => _messages.Count;

    /// <summary>获取或设置指定索引的消息</summary>
    public MessageDisplay this[int index]
    {
        get => _messages[index];
        set => _messages[index] = value;
    }

    /// <summary>获取内部消息列表（用于直接访问）</summary>
    public List<MessageDisplay> Items => _messages;

    // ========== 消息管理方法 ==========

    /// <summary>添加消息</summary>
    public void Add(MessageDisplay msg)
    {
        _messages.Add(msg);
        if (_messages.Count > MaxMessages)
            _messages.RemoveAt(0);
    }

    /// <summary>清空消息</summary>
    public void Clear()
    {
        _messages.Clear();
        CurrentStreamingMessage = null;
        CurrentToolCalls.Clear();
    }

    /// <summary>清空流式状态</summary>
    public void ClearStreamingState()
    {
        CurrentStreamingMessage = null;
        CurrentToolCalls.Clear();
    }

    // ========== IEnumerable<MessageDisplay> 实现 ==========

    public IEnumerator<MessageDisplay> GetEnumerator() => _messages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _messages.GetEnumerator();
}