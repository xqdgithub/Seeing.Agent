using System.Text;

namespace Seeing.Agent.Tui.Core.Models;

/// <summary>
/// 流式输出缓冲器 - 解决换行混乱、思考过程分离问题
/// </summary>
public class MessageBuffer
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly List<ToolCallDisplay> _toolCalls = new();
    private readonly List<FileAttachment> _attachments = new();
    
    /// <summary>是否有思考过程内容</summary>
    public bool HasReasoning => _reasoning.Length > 0;
    
    /// <summary>是否有正文内容</summary>
    public bool HasContent => _content.Length > 0;
    
    /// <summary>是否有工具调用</summary>
    public bool HasToolCalls => _toolCalls.Count > 0;
    
    /// <summary>思考过程内容</summary>
    public string Reasoning => _reasoning.ToString();
    
    /// <summary>正文内容</summary>
    public string Content => _content.ToString();
    
    /// <summary>追加正文内容</summary>
    public void AppendContent(string delta)
    {
        if (!string.IsNullOrEmpty(delta))
            _content.Append(delta);
    }
    
    /// <summary>追加思考过程</summary>
    public void AppendReasoning(string delta)
    {
        if (!string.IsNullOrEmpty(delta))
            _reasoning.Append(delta);
    }
    
    /// <summary>添加工具调用</summary>
    public void AddToolCall(ToolCallDisplay tc)
    {
        _toolCalls.Add(tc);
    }
    
    /// <summary>添加文件附件</summary>
    public void AddAttachment(FileAttachment att)
    {
        _attachments.Add(att);
    }
    
    /// <summary>构建完整消息</summary>
    public MessageDisplay Build(string role = "assistant")
    {
        return new MessageDisplay
        {
            Role = role,
            Content = _content.ToString(),
            Reasoning = _reasoning.ToString(),
            ToolCalls = _toolCalls.ToList(),
            Attachments = _attachments.ToList(),
            Timestamp = DateTime.Now,
            IsComplete = true
        };
    }
    
    /// <summary>构建部分消息（用于流式显示）</summary>
    public MessageDisplay BuildPartial()
    {
        return new MessageDisplay
        {
            Role = "assistant",
            Content = _content.ToString(),
            Reasoning = _reasoning.ToString(),
            ToolCalls = _toolCalls.ToList(),
            Timestamp = DateTime.Now,
            IsComplete = false
        };
    }
    
    /// <summary>清空缓冲区</summary>
    public void Clear()
    {
        _content.Clear();
        _reasoning.Clear();
        _toolCalls.Clear();
        _attachments.Clear();
    }
}