using System.Text;

namespace Seeing.Agent.Tui.Core.Models;

/// <summary>
/// 流式消息 - 用于实时渲染
/// </summary>
public class StreamingMessage
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly object _lock = new();

    /// <summary>是否有效（有内容）</summary>
    public bool HasContent => _content.Length > 0 || _reasoning.Length > 0;

    /// <summary>正文内容</summary>
    public string Content
    {
        get
        {
            lock (_lock)
                return _content.ToString();
        }
    }

    /// <summary>思考过程</summary>
    public string Reasoning
    {
        get
        {
            lock (_lock)
                return _reasoning.ToString();
        }
    }

    /// <summary>是否有思考过程</summary>
    public bool HasReasoning
    {
        get
        {
            lock (_lock)
                return _reasoning.Length > 0;
        }
    }

    /// <summary>是否有正文内容</summary>
    public bool HasContentText
    {
        get
        {
            lock (_lock)
                return _content.Length > 0;
        }
    }

    /// <summary>追加正文</summary>
    public void AppendContent(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        lock (_lock)
            _content.Append(delta);
    }

    /// <summary>追加思考过程</summary>
    public void AppendReasoning(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        lock (_lock)
            _reasoning.Append(delta);
    }

    /// <summary>清空</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _content.Clear();
            _reasoning.Clear();
        }
    }
}