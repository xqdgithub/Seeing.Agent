namespace Seeing.Agent.Abstractions.Prompts;

/// <summary>
/// 提示词分节贡献契约。模块可注册贡献者，按 <see cref="Order"/> 组装各分节内容。
/// <para>
/// 贡献者自述 <see cref="Heading"/> 与 <see cref="Placement"/>：构建器不认识任何具体分节名，
/// 仅按声明排序/定位。<see cref="Heading"/> 为空的贡献者会被记 Error 日志并跳过。
/// </para>
/// </summary>
public interface IPromptSectionContributor
{
    /// <summary>分节名称，应使用 <see cref="PromptSectionNames"/> 常量。</summary>
    string SectionName { get; }

    /// <summary>分节标题字面量（如 <c>"## Tools"</c>）；模板含同名标题行时注入其下方，否则按 <see cref="Placement"/> 追加。空则跳过本贡献者。</summary>
    string Heading { get; }

    /// <summary>放置声明：追加末尾或插入到某已命名分节之后。</summary>
    PromptSectionPlacement Placement { get; }

    /// <summary>同分节内排序（升序）。</summary>
    int Order { get; }

    /// <summary>构建本分节内容；返回 <c>null</c> 表示跳过。</summary>
    Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default);
}
