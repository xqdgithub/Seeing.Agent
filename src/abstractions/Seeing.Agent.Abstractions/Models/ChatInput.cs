namespace Seeing.Agent.Abstractions.Models;

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

    /// <summary>
    /// 内联媒体载荷，<b>必须是纯 base64（不含 <c>data:</c> 前缀</b>，不含 <c>;base64,</c> 标记）。
    /// <para>
    /// 该约束与下游 <c>SessionContentPart.DataBase64</c>、<c>ChatContentPart.DataBase64</c> 一致：
    /// provider 出口会自行拼 <c>data:{mime};base64,{...}</c>。若此处带前缀，出口将拼出双重前缀的非法载荷
    /// （provider 侧表现为 <c>Failed to download multimodal content</c>）。
    /// </para>
    /// <para>
    /// 例外：值不来自内联字节时可能是 https URL（当前 runtime 无独立 URL 字段）。
    /// </para>
    /// </summary>
    public string Base64Data { get; init; } = "";

    /// <summary>
    /// 本地文件绝对路径（引用式附件）。
    /// <para>
    /// 与 <see cref="Base64Data"/> 二选一：有路径时不传内联字节，框架在出站前读盘物化。
    /// 会话因此只存路径，不因附件膨胀（会话文件是整文件重写）。
    /// </para>
    /// </summary>
    public string? LocalPath { get; init; }

    /// <summary>
    /// 创建图片附件。传入 data URL 时会自动剥掉前缀，保证「纯 base64」不变量。
    /// </summary>
    public static ChatAttachment Image(string fileName, string base64Data, string mimeType = "image/png")
        => new() { FileName = fileName, MimeType = mimeType, Base64Data = StripDataPrefix(base64Data) };

    /// <summary>
    /// 创建文件附件。传入 data URL 时会自动剥掉前缀，保证「纯 base64」不变量。
    /// </summary>
    public static ChatAttachment File(string fileName, string base64Data, string mimeType)
        => new() { FileName = fileName, MimeType = mimeType, Base64Data = StripDataPrefix(base64Data) };

    /// <summary>
    /// 剥掉 data URL 的 <c>data:{mime};base64,</c> 前缀；非 data URL 原样返回。
    /// </summary>
    private static string StripDataPrefix(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        const string marker = ";base64,";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? value : value[(index + marker.Length)..];
    }
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
