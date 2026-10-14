namespace Seeing.Agent.Abstractions.Files;

/// <summary>目录项（文件或子目录）。</summary>
/// <param name="Id">资源标识。</param>
/// <param name="Name">名称（不含父路径）。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="Size">文件字节数；目录为 null。</param>
/// <param name="LastWriteUtc">最后写入时间；目录为 null。</param>
public sealed record FileEntry(
    FileResourceId Id,
    string Name,
    bool IsDirectory,
    long? Size,
    DateTime? LastWriteUtc);
