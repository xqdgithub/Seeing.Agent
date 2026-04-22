namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 会话视图模型，用于 UI 显示
/// </summary>
public class SessionViewModel
{
    /// <summary>
    /// 会话唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 会话标题
    /// </summary>
    public string Title { get; set; } = "新会话";

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 会话中的消息列表
    /// </summary>
    public List<MessageViewModel> Messages { get; set; } = new();

    /// <summary>
    /// 消息数量（便捷属性）
    /// </summary>
    public int MessageCount => Messages?.Count ?? 0;
}