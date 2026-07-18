using Seeing.Agent.Configuration;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Configuration;

/// <summary>
/// 基于 <see cref="IConfigSectionStore"/> 的 Memory 配置存储。
/// </summary>
public sealed class ConfigSectionMemoryOptionsStore : IMemoryOptionsStore
{
    public const string SectionName = "Memory";

    private readonly IConfigSectionStore _store;

    public ConfigSectionMemoryOptionsStore(IConfigSectionStore store)
    {
        _store = store;
    }

    public MemoryOptions Get() => _store.GetSection<MemoryOptions>(SectionName);

    public Task SaveAsync(MemoryOptions options, CancellationToken ct = default)
        => _store.SaveSectionAsync(SectionName, options, ConfigLevel.Project, ct);
}
