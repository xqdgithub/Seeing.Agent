namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 内容段视图模型，用于 UI 显示多模态消息内容
/// </summary>
public class ContentPartViewModel
{
    /// <summary>
    /// 段类型：text、image、file、audio
    /// </summary>
    public string Type { get; set; } = "text";

    /// <summary>
    /// 文本内容（text 类型）
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// 图片/文件 URL（用于显示）
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// 缩略图 URL（用于图片预览）
    /// </summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Base64 数据（用于内嵌显示）
    /// </summary>
    public string? DataBase64 { get; set; }

    /// <summary>
    /// MIME 类型
    /// </summary>
    public string? MimeType { get; set; }

    /// <summary>
    /// 文件名
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// 是否为图片
    /// </summary>
    public bool IsImage => Type == "image" || (MimeType?.StartsWith("image/") ?? false);

    /// <summary>
    /// 是否为文件
    /// </summary>
    public bool IsFile => Type == "file" && !IsImage;

    /// <summary>
    /// 是否为文本
    /// </summary>
    public bool IsText => Type == "text";

    /// <summary>
    /// 获取显示 URL（优先使用 ThumbnailUrl，否则使用 Url 或构造 data: URL）
    /// </summary>
    public string GetDisplayUrl()
    {
        if (!string.IsNullOrEmpty(ThumbnailUrl))
            return ThumbnailUrl;
        
        if (!string.IsNullOrEmpty(Url))
            return Url;
        
        if (!string.IsNullOrEmpty(DataBase64) && !string.IsNullOrEmpty(MimeType))
            return $"data:{MimeType};base64,{DataBase64}";
        
        return string.Empty;
    }

    /// <summary>
    /// 获取文件图标类型
    /// </summary>
    public string GetIconType()
    {
        if (IsImage)
            return "image";
        
        var ext = System.IO.Path.GetExtension(FileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".doc" or ".docx" => "word",
            ".xls" or ".xlsx" => "excel",
            ".ppt" or ".pptx" => "ppt",
            ".txt" => "text",
            ".zip" or ".rar" or ".7z" => "archive",
            ".mp3" or ".wav" or ".ogg" => "audio",
            ".mp4" or ".avi" or ".mov" => "video",
            ".json" or ".xml" or ".yaml" => "data",
            ".cs" or ".js" or ".py" or ".java" => "code",
            _ => "file"
        };
    }

    /// <summary>
    /// 从 SessionContentPart 创建视图模型
    /// </summary>
    public static ContentPartViewModel FromSessionPart(Seeing.Session.Core.SessionContentPart part)
    {
        return new ContentPartViewModel
        {
            Type = part.Type,
            Text = part.Text,
            Url = part.Url,
            DataBase64 = part.DataBase64,
            MimeType = part.MimeType,
            FileName = part.FileName
        };
    }

    /// <summary>
    /// 从 AttachmentViewModel 创建视图模型
    /// </summary>
    public static ContentPartViewModel FromAttachment(AttachmentViewModel attachment)
    {
        // 从 Base64Content 中提取纯 Base64 数据
        var base64Data = ExtractBase64Data(attachment.Base64Content);
        
        return new ContentPartViewModel
        {
            Type = attachment.IsImage ? "image" : "file",
            DataBase64 = base64Data,
            MimeType = attachment.FileType,
            FileName = attachment.FileName,
            FileSize = attachment.FileSize
        };
    }

    /// <summary>
    /// 从 Base64 字符串中提取纯 Base64 数据（去除 data: 前缀）
    /// </summary>
    private static string? ExtractBase64Data(string? base64Content)
    {
        if (string.IsNullOrEmpty(base64Content))
            return null;
        
        // 检查是否有 data: URL 前缀
        const string dataPrefix = "data:";
        if (base64Content.StartsWith(dataPrefix))
        {
            // 找到 base64, 的位置
            var base64Index = base64Content.IndexOf(";base64,");
            if (base64Index >= 0)
            {
                return base64Content.Substring(base64Index + 8);
            }
        }
        
        return base64Content;
    }
}