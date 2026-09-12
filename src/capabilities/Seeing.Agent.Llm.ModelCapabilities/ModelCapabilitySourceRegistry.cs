using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCapabilities;

/// <summary>
/// 能力源注册表实现（线程安全）。
/// </summary>
public sealed class ModelCapabilitySourceRegistry : IModelCapabilitySourceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IModelCapabilitySource> _sources =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? SourcesChanged;

    public void Register(IModelCapabilitySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Id))
            throw new ArgumentException("Source.Id 不能为空", nameof(source));

        lock (_gate)
            _sources[source.Id] = source;

        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Unregister(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return;

        lock (_gate)
        {
            if (!_sources.Remove(sourceId))
                return;
        }

        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<IModelCapabilitySource> GetSources()
    {
        lock (_gate)
            return _sources.Values.ToList();
    }
}
