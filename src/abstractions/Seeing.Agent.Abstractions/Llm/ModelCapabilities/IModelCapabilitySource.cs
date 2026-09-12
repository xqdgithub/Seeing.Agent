namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型能力源核心端口。
/// </summary>
public interface IModelCapabilitySource
{
    string Id { get; }
    string DisplayName { get; }

    ModelCapabilitySourceStatus GetStatus();

    ValueTask<ModelCapabilityEntry?> TryGetAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);

    string ResolveAlias(string providerId, string modelId);

    event EventHandler<ModelCapabilitySourceChangedEventArgs>? Changed;
}

public interface IListableModelCapabilitySource : IModelCapabilitySource
{
    /// <summary>
    /// 全量列举（可能昂贵；UI 应优先 <see cref="ListEntriesPageAsync"/>）。
    /// </summary>
    ValueTask<IReadOnlyList<ModelCapabilityEntry>> ListEntriesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页列举：实现方可流式/按页物化，避免一次装入全部条目。
    /// </summary>
    ValueTask<ModelCapabilityListPage> ListEntriesPageAsync(
        ModelCapabilityListQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ModelCapabilityAlias>> ListAliasesAsync(
        CancellationToken cancellationToken = default);
}

public interface IEditableModelCapabilitySource : IListableModelCapabilitySource
{
    ValueTask UpsertEntryAsync(ModelCapabilityEntry entry, CancellationToken cancellationToken = default);
    ValueTask DeleteEntryAsync(string? providerId, string modelId, CancellationToken cancellationToken = default);
    ValueTask UpsertAliasAsync(ModelCapabilityAlias alias, CancellationToken cancellationToken = default);
    ValueTask DeleteAliasAsync(string? providerId, string fromModelId, CancellationToken cancellationToken = default);
}

/// <summary>批量保存（一次 .bk + 一次写盘）。</summary>
public interface IBatchEditableModelCapabilitySource : IEditableModelCapabilitySource
{
    ValueTask ReplaceAllAsync(
        IReadOnlyList<ModelCapabilityEntry> entries,
        IReadOnlyList<ModelCapabilityAlias> aliases,
        CancellationToken cancellationToken = default);
}

public interface IRefreshableModelCapabilitySource : IModelCapabilitySource
{
    ValueTask RefreshAsync(CancellationToken cancellationToken = default);
}
