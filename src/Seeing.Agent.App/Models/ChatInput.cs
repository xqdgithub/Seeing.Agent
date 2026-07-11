namespace Seeing.Agent.App.Models;

/// <summary>
/// 聊天输入 - 用户发送的内容
/// </summary>
public record ChatInput
{
    /// <summary>文本内容</summary>
    public string? Text { get; init; }
    
    /// <summary>附件列表</summary>
    public List<ChatAttachment>? Attachments { get; init; }
    
    /// <summary>引用消息上下文</summary>
    public ChatQuoteContext? Quote { get; init; }
    
    /// <summary>
    /// 创建纯文本输入
    /// </summary>
    public static ChatInput FromText(string text) => new() { Text = text };
}

/// <summary>
/// 聊天附件
/// </summary>
public record ChatAttachment
{
    /// <summary>文件名</summary>
    public string FileName { get; init; } = "";
    
    /// <summary>MIME 类型</summary>
    public string MimeType { get; init; } = "";
    
    /// <summary>Base64 编码的数据</summary>
    public string Base64Data { get; init; } = "";
    
    /// <summary>
    /// 创建图片附件
    /// </summary>
    public static ChatAttachment Image(string fileName, string base64Data, string mimeType = "image/png")
        => new() { FileName = fileName, MimeType = mimeType, Base64Data = base64Data };
    
    /// <summary>
    /// 创建文件附件
    /// </summary>
    public static ChatAttachment File(string fileName, string base64Data, string mimeType)
        => new() { FileName = fileName, MimeType = mimeType, Base64Data = base64Data };
}

/// <summary>
/// 引用消息上下文
/// </summary>
public record ChatQuoteContext
{
    /// <summary>引用的消息 ID</summary>
    public string? MessageId { get; init; }
    
    /// <summary>引用的文本片段</summary>
    public string? Text { get; init; }
}
