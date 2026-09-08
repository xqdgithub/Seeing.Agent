namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 文件变更事件参数
/// </summary>
public class FileChangeEventArgs : EventArgs
{
    /// <summary>文件路径</summary>
    public string Path { get; }
    
    /// <summary>变更类型</summary>
    public FileChangeType Type { get; }
    
    /// <summary>时间戳</summary>
    public DateTimeOffset Timestamp { get; }
    
    public FileChangeEventArgs(string path, FileChangeType type)
    {
        Path = path;
        Type = type;
        Timestamp = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// 文件变更类型
/// </summary>
public enum FileChangeType
{
    Created,
    Modified,
    Deleted
}
