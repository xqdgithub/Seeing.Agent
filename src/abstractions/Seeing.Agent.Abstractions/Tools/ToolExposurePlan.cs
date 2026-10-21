using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具暴露计划：策略计算后的本轮内联 schema 集与目录条目集（纯数据）。
/// </summary>
/// <param name="InlineSchemas">内联进 tools 数组的完整 schema。</param>
/// <param name="CatalogEntries">延迟暴露的目录条目。</param>
/// <param name="InlineToolCount">内联工具数。</param>
/// <param name="DeferredToolCount">延迟工具数。</param>
/// <param name="EstimatedDefinitionTokens">内联定义估算 token 数。</param>
public sealed record ToolExposurePlan(
    IReadOnlyList<FunctionToolSchema> InlineSchemas,
    IReadOnlyList<ToolCatalogEntry> CatalogEntries,
    int InlineToolCount,
    int DeferredToolCount,
    int EstimatedDefinitionTokens);
