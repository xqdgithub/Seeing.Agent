namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 收件箱通知视图模型（由 <see cref="Seeing.Agent.Abstractions.Events.InboxNoticeEvent"/> 投影）。
/// 仅 UI 展示，不落盘、不进 LLM 历史。
/// </summary>
public class InboxNoticeViewModel
{
    public string SessionId { get; set; } = string.Empty;

    /// <summary>发送方类型：human | session | system。</summary>
    public string SenderKind { get; set; } = string.Empty;

    public string? SenderId { get; set; }
    public string? SourceTag { get; set; }
    public string? Kind { get; set; }
    public string? Body { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>发送方显示名（子会话/系统）。</summary>
    public string SenderDisplay => SenderKind.ToLowerInvariant() switch
    {
        "session" => "子会话",
        "system" => "系统",
        _ => "通知"
    };

    /// <summary>通知文案（正文为空时回退为来源描述）。</summary>
    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Body))
                return Body!.Trim();
            var kind = string.IsNullOrEmpty(Kind) ? "" : $"（{Kind}）";
            return $"{SenderDisplay}{kind} 有新消息";
        }
    }
}
