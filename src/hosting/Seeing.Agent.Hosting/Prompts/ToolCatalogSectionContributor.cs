using Seeing.Agent.Abstractions.Prompts;

namespace Seeing.Agent.Hosting.Prompts;

/// <summary>
/// 工具目录分节贡献者：透传执行链渲染好的目录正文（<see cref="PromptContext.Sections"/>["tool-catalog"]）。
/// 无内容时返回 null，builder 不输出该分节。渲染逻辑本身在 <c>ToolCatalogRenderer</c>（单点）。
/// </summary>
public sealed class ToolCatalogSectionContributor : IPromptSectionContributor
{
    /// <summary>目录正文在 <see cref="PromptContext.Sections"/> 中的键。</summary>
    public const string CatalogSectionKey = "tool-catalog";

    /// <inheritdoc />
    public string SectionName => CatalogSectionKey;

    /// <inheritdoc />
    public string Heading => "## Tool Catalog";

    /// <inheritdoc />
    public PromptSectionPlacement Placement => PromptSectionPlacement.AtEnd;

    /// <inheritdoc />
    public int Order => 150;

    /// <inheritdoc />
    public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        var text = context.Sections.TryGetValue(CatalogSectionKey, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
        return Task.FromResult(text);
    }
}
