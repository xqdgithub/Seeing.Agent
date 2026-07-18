using Seeing.Agent.Memory.Core.Storage;

namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// 文件节点 - 代表一个记忆文件
/// </summary>
public record FileNode(
    string Path,                    // 相对路径 (如: daily/2025-01-15/session-abc.md)
    string Content,                 // Markdown 内容
    DateTimeOffset ModifiedTime,    // 修改时间
    FileMetadata Metadata,          // YAML frontmatter 元数据
    IReadOnlyList<string> Links     // [[wikilink]] 列表
)
{
    /// <summary>从内容和路径创建 FileNode</summary>
    public static FileNode Create(string path, string content, FileMetadata? metadata = null)
    {
        var links = ParseWikilinks(content);
        var meta = metadata ?? FileMetadata.Create(
            Guid.NewGuid().ToString("N")[..8],
            InferType(path)
        );
        
        return new FileNode(
            Path: path,
            Content: content,
            ModifiedTime: DateTimeOffset.UtcNow,
            Metadata: meta,
            Links: links
        );
    }
    
    /// <summary>解析 Wikilink</summary>
    private static IReadOnlyList<string> ParseWikilinks(string content)
    {
        return WikilinkParser.Parse(content);
    }
    
    private static MemoryType InferType(string path)
    {
        if (path.StartsWith("session/")) return MemoryType.Session;
        if (path.StartsWith("daily/")) return MemoryType.Daily;
        if (path.StartsWith("digest/")) return MemoryType.Digest;
        return MemoryType.Daily;
    }
}
