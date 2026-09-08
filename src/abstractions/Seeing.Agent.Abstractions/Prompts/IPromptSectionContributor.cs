namespace Seeing.Agent.Abstractions.Prompts;

/// <summary>
/// 提示词分节贡献契约。模块可注册贡献者，按 <see cref="Order"/> 组装各分节内容。
/// </summary>
public interface IPromptSectionContributor
{
    /// <summary>分节名称，应使用 <see cref="PromptSectionNames"/> 常量。</summary>
    string SectionName { get; }

    /// <summary>同分节内排序（升序）。</summary>
    int Order { get; }

    /// <summary>构建本分节内容；返回 <c>null</c> 表示跳过。</summary>
    Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default);
}
