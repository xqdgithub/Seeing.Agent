namespace Seeing.Agent.Files;

/// <summary>文件过大，无法作为可编辑全文加载。</summary>
public sealed class FileTooLargeException : Exception
{
    /// <summary>构造。</summary>
    public FileTooLargeException(string path, long size, long limit)
        : base($"文件过大（{size} 字节，上限 {limit}）：{path}") { }
}

/// <summary>文件编码不支持编辑写入。</summary>
public sealed class FileNotEditableException : Exception
{
    /// <summary>构造。</summary>
    public FileNotEditableException(string path, string reason)
        : base($"文件不可编辑（{reason}）：{path}") { }
}
