using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;

namespace Seeing.Agent.SystemOne.Tests;

/// <summary>内存版 <see cref="IConfigSectionStore"/>；缺失节返回 null 以模拟真实兜底路径。</summary>
internal sealed class TestConfigSectionStore : IConfigSectionStore
{
    private readonly Dictionary<string, object> _sections = new(StringComparer.OrdinalIgnoreCase);

    public TestConfigSectionStore()
    {
    }

    public TestConfigSectionStore(Dictionary<string, SystemOneProviderConfig> systemOne)
        => _sections[SystemOneConfigStore.SectionName] = systemOne;

    public T GetSection<T>(string sectionName) where T : class, new()
        => _sections.TryGetValue(sectionName, out var value) && value is T typed ? typed : default!;

    public Task SaveSectionAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
        where T : class
    {
        _sections[sectionName] = value!;
        return Task.CompletedTask;
    }

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
    {
        add { }
        remove { }
    }
}
