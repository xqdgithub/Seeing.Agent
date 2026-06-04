namespace Seeing.Agent.WebUI.Models.Messaging;

/// <summary>
/// 消息内容类型枚举
/// </summary>
public enum MessageType
{
    /// <summary>
    /// 用户消息
    /// </summary>
    User,

    /// <summary>
    /// 助手消息
    /// </summary>
    Assistant,

    /// <summary>
    /// 系统消息
    /// </summary>
    System,

    /// <summary>
    /// 工具消息
    /// </summary>
    Tool,

    /// <summary>
    /// 思考/推理过程
    /// </summary>
    Reasoning,

    /// <summary>
    /// 工具调用
    /// </summary>
    ToolCall,

    /// <summary>
    /// 附件内容
    /// </summary>
    Attachment,

    /// <summary>
    /// Loop 分组
    /// </summary>
    LoopGroup
}

/// <summary>
/// 消息状态枚举
/// </summary>
public enum MessageStatus
{
    /// <summary>
    /// 待处理
    /// </summary>
    Pending,

    /// <summary>
    /// 处理中
    /// </summary>
    Streaming,

    /// <summary>
    /// 已完成
    /// </summary>
    Complete,

    /// <summary>
    /// 错误
    /// </summary>
    Error
}
