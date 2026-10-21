namespace Seeing.Agent.Abstractions.Prompts;

/// <summary>
/// 提示词分节放置声明（可辨识联合风格）：要么追加到 prompt 末尾（<see cref="AppendAtEnd"/> 为 <c>true</c>），
/// 要么插入到某个已命名分节之后（<see cref="AppendAtEnd"/> 为 <c>false</c> 且 <see cref="AfterSection"/> 指向该分节的 SectionName）。
/// 构建器不认识任何具体分节名，仅按此声明定位；找不到锚点分节时退化为末尾追加。
/// </summary>
public sealed record PromptSectionPlacement
{
    /// <summary>是否追加到 prompt 末尾。</summary>
    public bool AppendAtEnd { get; init; }

    /// <summary>插入到哪个分节之后（仅当 <see cref="AppendAtEnd"/> 为 <c>false</c> 时有效，值为某分节的 SectionName）。</summary>
    public string? AfterSection { get; init; }

    /// <summary>追加到 prompt 末尾（按既有顺序，即 Order 升序）。</summary>
    public static PromptSectionPlacement AtEnd { get; } = new() { AppendAtEnd = true };

    /// <summary>插入到 <paramref name="sectionName"/> 命名的分节内容之后。</summary>
    public static PromptSectionPlacement After(string sectionName) =>
        new() { AppendAtEnd = false, AfterSection = sectionName };
}
