namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 提交事件参数
/// </summary>
public class PromptSubmitEventArgs
{
    /// <summary>
    /// 输入文本
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// 附件列表
    /// </summary>
    public List<AttachmentViewModel> Attachments { get; set; } = new();
}

/// <summary>
/// JSInterop 文件信息结构
/// </summary>
public class FileInfoJsInterop
{
    /// <summary>
    /// 文件基本信息
    /// </summary>
    public FileBasicInfo Info { get; set; } = new();

    /// <summary>
    /// Base64 编码内容
    /// </summary>
    public string Base64 { get; set; } = "";
}

/// <summary>
/// 文件基本信息
/// </summary>
public class FileBasicInfo
{
    /// <summary>
    /// 文件名
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 文件大小
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// MIME 类型
    /// </summary>
    public string Type { get; set; } = "";
}