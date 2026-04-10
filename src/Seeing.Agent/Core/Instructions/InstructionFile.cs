namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令文件模型
/// </summary>
public class InstructionFile
{
    /// <summary>文件路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>文件内容</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>最后修改时间</summary>
    public DateTimeOffset LastModified { get; set; }
}