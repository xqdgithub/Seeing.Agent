using Microsoft.Extensions.Options;

namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 从 <see cref="IConfigSectionStore"/> 桥接任意配置节到 <see cref="IOptionsMonitor{TOptions}"/>。
/// </summary>
public sealed class ConfigSectionOptionsMonitor<TOptions> : IOptions<TOptions>, IOptionsMonitor<TOptions>
    where TOptions : class, new()
{
    private readonly IConfigSectionStore _store;
    private readonly string _sectionName;
    private readonly List<Action<TOptions, string?>> _listeners = new();

    public ConfigSectionOptionsMonitor(IConfigSectionStore store, string sectionName)
    {
        _store = store;
        _sectionName = sectionName;
        _store.ConfigChanged += OnConfigChanged;
    }

    public TOptions Value => CurrentValue;

    public TOptions CurrentValue => _store.GetSection<TOptions>(_sectionName);

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        lock (_listeners)
            _listeners.Add(listener);
        return new Disposable(() =>
        {
            lock (_listeners)
                _listeners.Remove(listener);
        });
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.ChangedSections is { Length: > 0 } &&
            !e.ChangedSections.Contains(_sectionName, StringComparer.OrdinalIgnoreCase))
            return;

        var value = CurrentValue;
        lock (_listeners)
        {
            foreach (var listener in _listeners)
            {
                try { listener(value, null); }
                catch { /* ignore */ }
            }
        }
    }

    private sealed class Disposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
