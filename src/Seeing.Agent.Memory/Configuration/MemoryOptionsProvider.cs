using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Configuration;

/// <summary>
/// 从 <see cref="IConfigSectionStore"/> 提供 <see cref="MemoryOptions"/>（IOptions / IOptionsMonitor）。
/// </summary>
public sealed class MemoryOptionsProvider : IOptionsMonitor<MemoryOptions>, IMemoryOptionsStore, IDisposable
{
    private readonly IConfigSectionStore _store;
    private readonly ILogger<MemoryOptionsProvider>? _logger;
    private MemoryOptions _current = new();
    private event Action<MemoryOptions, string?>? _listeners;

    public MemoryOptionsProvider(
        IConfigSectionStore store,
        ILogger<MemoryOptionsProvider>? logger = null)
    {
        _store = store;
        _logger = logger;
        Reload();
        _store.ConfigChanged += OnConfigChanged;
    }

    public MemoryOptions CurrentValue => _current;
    public MemoryOptions Get(string? name) => _current;

    public MemoryOptions Get() => _current;

    public async Task SaveAsync(MemoryOptions options, CancellationToken ct = default)
    {
        await _store.SaveSectionAsync(ConfigSectionMemoryOptionsStore.SectionName, options, ConfigLevel.Project, ct);
        Reload();
    }

    public IDisposable OnChange(Action<MemoryOptions, string?> listener)
    {
        _listeners += listener;
        return new ChangeSubscription(() => _listeners -= listener);
    }

    public void Reload()
    {
        _current = _store.GetSection<MemoryOptions>(ConfigSectionMemoryOptionsStore.SectionName);
        _logger?.LogDebug(
            "Memory options reloaded (Enabled={Enabled}, EmbeddingConfigured={EmbeddingConfigured})",
            _current.Enabled,
            _current.IsEmbeddingConfigured);
        _listeners?.Invoke(_current, Options.DefaultName);
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.ContainsSection(ConfigSectionMemoryOptionsStore.SectionName))
            Reload();
    }

    public void Dispose()
    {
        _store.ConfigChanged -= OnConfigChanged;
    }

    private sealed class ChangeSubscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

public sealed class MemoryOptionsAccessor : IOptions<MemoryOptions>
{
    private readonly MemoryOptionsProvider _provider;
    public MemoryOptionsAccessor(MemoryOptionsProvider provider) => _provider = provider;
    public MemoryOptions Value => _provider.CurrentValue;
}
