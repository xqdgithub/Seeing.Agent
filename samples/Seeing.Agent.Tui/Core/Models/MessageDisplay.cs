namespace Seeing.Agent.Tui.Core.Models;

/// <summary>
/// 消息展示模型 - 用于UI渲染
/// </summary>
public class MessageDisplay
{
    /// <summary>角色: user, assistant, tool, system</summary>
    public string Role { get; set; } = "";
    
    /// <summary>正文内容</summary>
    public string Content { get; set; } = "";
    
    /// <summary>思考过程（Reasoning）</summary>
    public string Reasoning { get; set; } = "";
    
    /// <summary>工具调用列表</summary>
    public List<ToolCallDisplay> ToolCalls { get; set; } = new();
    
    /// <summary>文件附件</summary>
    public List<FileAttachment> Attachments { get; set; } = new();
    
    /// <summary>时间戳</summary>
    public DateTime Timestamp { get; set; }
    
    /// <summary>是否完成（流式输出时使用）</summary>
    public bool IsComplete { get; set; }
    
    /// <summary>工具调用ID（用于tool消息）</summary>
    public string? ToolCallId { get; set; }
    
    /// <summary>工具执行结果</summary>
    public string? ToolResult { get; set; }
    
    /// <summary>工具执行是否成功</summary>
    public bool ToolSuccess { get; set; }
}

/// <summary>
/// 工具调用展示模型
/// </summary>
public class ToolCallDisplay
{
    /// <summary>调用ID</summary>
    public string Id { get; set; } = "";
    
    /// <summary>工具名称</summary>
    public string Name { get; set; } = "";
    
    /// <summary>参数（JSON格式）</summary>
    public string Arguments { get; set; } = "";
    
    /// <summary>执行状态</summary>
    public Seeing.Agent.Core.Events.ToolCallStatus Status { get; set; } = Seeing.Agent.Core.Events.ToolCallStatus.Pending;
    
    /// <summary>执行结果</summary>
    public string? Result { get; set; }
    
    /// <summary>是否成功</summary>
    public bool Success => Status == Seeing.Agent.Core.Events.ToolCallStatus.Success;
    
    /// <summary>开始时间</summary>
    public DateTime StartTime { get; set; }
    
    /// <summary>结束时间</summary>
    public DateTime? EndTime { get; set; }
    
    /// <summary>执行耗时</summary>
    public TimeSpan? Duration => EndTime - StartTime;
    
    /// <summary>错误信息</summary>
    public string? Error { get; set; }
    
    /// <summary>状态图标</summary>
    public string StatusIcon => Status switch
    {
        Seeing.Agent.Core.Events.ToolCallStatus.Pending => "[yellow]⏳[/]",
        Seeing.Agent.Core.Events.ToolCallStatus.Running => "[blue]🔄[/]",
        Seeing.Agent.Core.Events.ToolCallStatus.Success => "[green]✓[/]",
        Seeing.Agent.Core.Events.ToolCallStatus.Failed => "[red]✗[/]",
        Seeing.Agent.Core.Events.ToolCallStatus.Rejected => "[grey]⊘[/]",
        _ => "[grey]?[/]"
    };
    
    /// <summary>状态颜色</summary>
    public string StatusColor => Status switch
    {
        Seeing.Agent.Core.Events.ToolCallStatus.Pending => "yellow",
        Seeing.Agent.Core.Events.ToolCallStatus.Running => "blue",
        Seeing.Agent.Core.Events.ToolCallStatus.Success => "green",
        Seeing.Agent.Core.Events.ToolCallStatus.Failed => "red",
        Seeing.Agent.Core.Events.ToolCallStatus.Rejected => "grey",
        _ => "white"
    };
}

/// <summary>
/// 文件附件
/// </summary>
public class FileAttachment
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ContentType { get; set; }
    public long? Size { get; set; }
}