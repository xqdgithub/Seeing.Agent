using Seeing.Agent.Abstractions.Agents;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具检索语料端口：返回某会话+Agent 当前"可发现但尚未内联"的工具目录（即 deferred 条目），
/// 作为 <c>tool_search</c> 的检索索引。单源由 schema 结算出口驱动，避免能力包自行重算 settle 口径。
/// <para>
/// full 级详情（完整定义文本）由消费方经 <see cref="IToolManager"/> 按 id 权威获取，不在此重复承载。
/// </para>
/// </summary>
public interface IToolSearchProvider
{
    /// <summary>可发现的目录条目（id / namespace / 截断摘要 / 估算 token）。空集表示无可发现工具或参数缺失。</summary>
    Task<IReadOnlyList<ToolCatalogEntry>> GetDiscoverableAsync(
        string sessionId,
        AgentDefinition? agent,
        CancellationToken cancellationToken = default);
}
