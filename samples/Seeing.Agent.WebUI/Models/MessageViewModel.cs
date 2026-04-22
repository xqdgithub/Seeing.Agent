namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 消息视图模型，用于 UI 显示
/// </summary>
public class MessageViewModel
{
    /// <summary>
    /// 消息唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 消息角色 (user/assistant/system/tool)
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// 消息内容
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// 推理过程（如果支持）
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// 消息时间戳
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 工具调用列表
    /// </summary>
    public List<ToolCallViewModel> ToolCalls { get; set; } = new();

    /// <summary>
    /// 多模态内容段列表（图片、文件等附件）
    /// </summary>
    public List<ContentPartViewModel> Parts { get; set; } = new();

    /// <summary>
    /// 消息是否已完成
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// 是否包含附件
    /// </summary>
    public bool HasAttachments => Parts != null && Parts.Count > 0;

    /// <summary>
    /// 是否包含图片
    /// </summary>
    public bool HasImages => Parts?.Any(p => p.IsImage) ?? false;

    /// <summary>
    /// 是否包含文件（非图片）
    /// </summary>
    public bool HasFiles => Parts?.Any(p => p.IsFile) ?? false;
}