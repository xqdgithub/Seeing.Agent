namespace Seeing.Agent.Abstractions.Files;

/// <summary>稳定文件资源标识：根哈希 + 相对路径（统一 '/' 分隔）。</summary>
/// <param name="RootId">有效根绝对路径规范化哈希。</param>
/// <param name="RelativePath">相对根的路径，统一 '/' 分隔。</param>
public readonly record struct FileResourceId(string RootId, string RelativePath)
{
    /// <inheritdoc />
    public override string ToString() => $"{RootId}:{RelativePath}";
}
