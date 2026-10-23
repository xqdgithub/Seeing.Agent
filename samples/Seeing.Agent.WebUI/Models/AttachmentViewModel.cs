namespace Seeing.Agent.WebUI.Models;

/// <summary>附件投递方式。</summary>
public enum AttachmentDeliveryKind
{
    /// <summary>图片等二进制附件，提交时进入 ChatAttachment。</summary>
    BinaryAttachment = 0,

    /// <summary>普通文件，提交时把路径写入消息正文。</summary>
    PathReference = 1
}

/// <summary>
/// 文件附件视图模型
/// </summary>
public class AttachmentViewModel
{
    /// <summary>投递方式。</summary>
    public AttachmentDeliveryKind Kind { get; set; } = AttachmentDeliveryKind.BinaryAttachment;

    /// <summary>提交时写入正文的路径（项目根相对或绝对）。</summary>
    public string? PromptPath { get; set; }

    /// <summary>相对会话有效根的路径，用于删除/定位。</summary>
    public string? SessionRelativePath { get; set; }

    /// <summary>是否路径引用。</summary>
    public bool IsPathReference => Kind == AttachmentDeliveryKind.PathReference;

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