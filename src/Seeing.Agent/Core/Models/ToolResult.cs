namespace Seeing.Agent.Core.Models;

/// <summary>
/// 文件附件
/// </summary>
public class FileAttachment
{
    /// <summary>文件名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>文件路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>MIME 类型</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    public long? Size { get; set; }
}

/// <summary>
/// 工具执行结果 - 统一的工具调用返回类型
/// <para>
/// 字段命名参考业界惯例：
/// - OpenAI/Anthropic: content
/// - LangChain: output, error
/// - Semantic Kernel: result
/// </para>
/// </summary>
public class ToolResult
{
    /// <summary>是否成功执行</summary>
    public bool Success { get; set; }

    /// <summary>
    /// 输出内容（成功时）
    /// <para>对应 OpenAI 的 content，LangChain 的 output</para>
    /// </summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>标题/工具调用名称（用于显示与追踪）</summary>
    public string? Title { get; set; }

    /// <summary>
    /// 错误信息（失败时）
    /// <para>对应 LangChain 的 error</para>
    /// </summary>
    public string? Error { get; set; }

    /// <summary>关联的工具调用 ID</summary>
    public string? ToolCallId { get; set; }

    /// <summary>元数据</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>文件附件</summary>
    public List<FileAttachment>? Attachments { get; set; }

    /// <summary>执行耗时</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    public static ToolResult Succeeded(string output)
    {
        return new ToolResult
        {
            Success = true,
            Output = output
        };
    }

    /// <summary>
    /// 创建失败结果
    /// </summary>
    public static ToolResult Failed(string error)
    {
        return new ToolResult
        {
            Success = false,
            Error = error
        };
    }
}
