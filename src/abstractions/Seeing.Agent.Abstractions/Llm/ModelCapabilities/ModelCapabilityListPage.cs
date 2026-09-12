namespace Seeing.Agent.Abstractions.Llm;

/// <summary>能力条目分页查询。</summary>
public sealed class ModelCapabilityListQuery
{
    /// <summary>跳过条数（≥0）。</summary>
    public int Skip { get; init; }

    /// <summary>取条数（默认 20，最大 100）。</summary>
    public int Take { get; init; } = 20;

    /// <summary>可选：ModelId / ProviderId / Name 子串（忽略大小写）。</summary>
    public string? Filter { get; init; }

    /// <summary>可选：精确 ProviderId 过滤。</summary>
    public string? ProviderId { get; init; }
}

/// <summary>能力条目分页结果（仅含本页，勿假定源已全量驻留内存）。</summary>
public sealed class ModelCapabilityListPage
{
    public IReadOnlyList<ModelCapabilityEntry> Items { get; init; } = [];
    public int Total { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}
