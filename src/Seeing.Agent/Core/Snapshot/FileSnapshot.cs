namespace Seeing.Agent.Core.Snapshot;

/// <summary>
/// 文件快照模型
/// </summary>
public class FileSnapshot
{
    /// <summary>快照 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>文件路径</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>文件内容</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>操作类型（如：create, update, delete）</summary>
    public string? Operation { get; set; }

    /// <summary>关联的会话 ID</summary>
    public string? SessionId { get; set; }
}