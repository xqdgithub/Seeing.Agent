namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 目录命名空间分组（显示层）：每命名空间恒含总数与已展示条目（可为空），
/// 满足「命名空间行 MUST 含名称与总数、未完整展示 MUST 标注已展示数量、0 展示仍出现」的契约。
/// 与「检索语料」（全量 deferred）正交——预算裁剪只影响此显示分组，不影响可发现性。
/// </summary>
public sealed record ToolCatalogNamespace(
    string Namespace,
    int TotalCount,
    IReadOnlyList<ToolCatalogEntry> Shown);
