namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 文件附件视图模型
/// </summary>
public class AttachmentViewModel
{
    /// <summary>
    /// 附件唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 文件名
    /// </summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 文件 MIME 类型
    /// </summary>
    public string FileType { get; set; } = "";

    /// <summary>
    /// Base64 编码的内容
    /// </summary>
    public string Base64Content { get; set; } = "";

    /// <summary>
    /// 是否为图片
    /// </summary>
    public bool IsImage => FileType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 上传时间
    /// </summary>
    public DateTime UploadTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 上传状态
    /// </summary>
    public AttachmentUploadStatus Status { get; set; } = AttachmentUploadStatus.Pending;

    /// <summary>
    /// 错误消息（上传失败时）
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 获取格式化的文件大小
    /// </summary>
    public string GetFormattedSize()
    {
        if (FileSize < 1024)
            return $"{FileSize}B";
        if (FileSize < 1024 * 1024)
            return $"{FileSize / 1024:F1}KB";
        if (FileSize < 1024 * 1024 * 1024)
            return $"{FileSize / (1024 * 1024):F1}MB";
        return $"{FileSize / (1024 * 1024 * 1024):F1}GB";
    }

    /// <summary>
    /// 获取文件图标类型
    /// </summary>
    public string GetIconType()
    {
        if (IsImage)
            return "image";

        var ext = System.IO.Path.GetExtension(FileName).ToLowerInvariant();
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
}

/// <summary>
/// 附件上传状态
/// </summary>
public enum AttachmentUploadStatus
{
    /// <summary>
    /// 待上传
    /// </summary>
    Pending,

    /// <summary>
    /// 上传中
    /// </summary>
    Uploading,

    /// <summary>
    /// 已完成
    /// </summary>
    Completed,

    /// <summary>
    /// 失败
    /// </summary>
    Failed
}