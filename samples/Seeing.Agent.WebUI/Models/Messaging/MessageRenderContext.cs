namespace Seeing.Agent.WebUI.Models.Messaging;

/// <summary>
/// 消息组件渲染上下文 - 包含渲染所需的所有数据
/// </summary>
public class MessageRenderContext
{
    /// <summary>
    /// 消息 ID
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// 消息类型
    /// </summary>
    public MessageType Type { get; set; }

    /// <summary>
    /// 消息状态
    /// </summary>
    public MessageStatus Status { get; set; }

    /// <summary>
    /// 角色名称 (user/assistant/system/tool)
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// 文本内容
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// 推理/思考内容
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 是否已完成
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// 工具调用列表
    /// </summary>
    public List<ToolCallViewModel> ToolCalls { get; set; } = new();

    /// <summary>
    /// 附件列表
    /// </summary>
    public List<ContentPartViewModel> Attachments { get; set; } = new();

    /// <summary>
    /// Loop ID（用于分组）
    /// </summary>
    public string? LoopId { get; set; }

    /// <summary>
    /// 步骤索引
    /// </summary>
    public int Step { get; set; }

    /// <summary>
    /// 扩展数据（用于传递额外信息）
    /// </summary>
    public Dictionary<string, object> Extensions { get; set; } = new();

    /// <summary>
    /// 从 MessageViewModel 创建渲染上下文
    /// </summary>
    public static MessageRenderContext FromMessage(MessageViewModel message)
    {
        return new MessageRenderContext
        {
            MessageId = message.Id,
            Type = DetermineMessageType(message),
            Status = DetermineMessageStatus(message),
            Role = message.Role,
            Content = message.Content,
            Reasoning = message.Reasoning,
            Timestamp = message.Timestamp,
            IsComplete = message.IsComplete,
            ToolCalls = message.ToolCalls,
            Attachments = message.Parts,
            LoopId = message.LoopId,
            Step = message.Step
        };
    }

    /// <summary>
    /// 确定消息类型
    /// </summary>
    private static MessageType DetermineMessageType(MessageViewModel message)
    {
        return message.Role.ToLowerInvariant() switch
        {
            "user" => MessageType.User,
            "assistant" => MessageType.Assistant,
            "system" => MessageType.System,
            "tool" => MessageType.Tool,
            _ => MessageType.User
        };
    }

    /// <summary>
    /// 确定消息状态
    /// </summary>
    private static MessageStatus DetermineMessageStatus(MessageViewModel message)
    {
        if (!message.IsComplete)
            return MessageStatus.Streaming;

        if (message.ToolCalls.Any(t => !string.IsNullOrEmpty(t.Error)))
            return MessageStatus.Error;

        return MessageStatus.Complete;
    }
}
