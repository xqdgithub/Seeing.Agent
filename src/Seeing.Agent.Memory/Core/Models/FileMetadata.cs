namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// 文件元数据 (YAML frontmatter)
/// </summary>
public record FileMetadata(
    string Id,                      // 唯一 ID
    MemoryType Type,                // 类型
    string? Title,                  // 标题
    IReadOnlyList<string> Tags,     // 标签
    double Importance,              // 重要性 0.0-1.0
    double Confidence,              // 置信度 0.0-1.0
    DateTimeOffset CreatedAt,       // 创建时间
    DateTimeOffset? ExpiresAt       // 过期时间
)
{
    /// <summary>默认构造</summary>
    public static FileMetadata Create(string id, MemoryType type, string? title = null)
    {
        return new FileMetadata(
            Id: id,
            Type: type,
            Title: title,
            Tags: Array.Empty<string>(),
            Importance: 0.5,
            Confidence: 1.0,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: null
        );
    }
}
