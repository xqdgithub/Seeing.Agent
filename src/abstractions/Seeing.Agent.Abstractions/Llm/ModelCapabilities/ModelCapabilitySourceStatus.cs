namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 能力源运行状态（列表/编辑/刷新能力由 is IListable/IEditable/IRefreshable 判定）。
/// </summary>
public sealed class ModelCapabilitySourceStatus
{
    public DateTimeOffset? LastLoadedAt { get; init; }
    public int EntryCount { get; init; }
    public int AliasCount { get; init; }
    public string? LastError { get; init; }
}
