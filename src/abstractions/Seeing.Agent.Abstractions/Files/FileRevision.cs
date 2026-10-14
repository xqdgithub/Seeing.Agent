namespace Seeing.Agent.Abstractions.Files;

/// <summary>文件修订号：全文 SHA256 + 大小 + 最后写入时间。</summary>
/// <param name="Hash">全文内容的 SHA256（十六进制小写）。</param>
/// <param name="Size">文件字节数。</param>
/// <param name="LastWriteUtc">最后写入时间（UTC）。</param>
public readonly record struct FileRevision(string Hash, long Size, DateTime LastWriteUtc)
{
    /// <summary>与另一修订号是否相等；null 视为不匹配。</summary>
    public bool Matches(FileRevision? other) =>
        other is { } o && o.Size == Size
        && string.Equals(o.Hash, Hash, StringComparison.Ordinal);
}
