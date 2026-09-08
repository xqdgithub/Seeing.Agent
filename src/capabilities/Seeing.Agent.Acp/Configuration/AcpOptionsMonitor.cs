using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// 从 <see cref="IConfigSectionStore"/> 桥接 <see cref="AcpOptions"/> 的 IOptions / IOptionsMonitor。
/// </summary>
public sealed class AcpOptionsMonitor : IOptions<AcpOptions>, IOptionsMonitor<AcpOptions>
{
    private readonly IConfigSectionStore _store;
    private readonly List<Action<AcpOptions, string?>> _listeners = new();

    public AcpOptionsMonitor(IConfigSectionStore store)
    {
        _store = store;
        _store.ConfigChanged += OnConfigChanged;
    }

    public AcpOptions Value => CurrentValue;

    public AcpOptions CurrentValue => _store.GetSection<AcpOptions>(AcpOptions.SectionName);

    public AcpOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<AcpOptions, string?> listener)
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
            !e.ChangedSections.Contains(AcpOptions.SectionName, StringComparer.OrdinalIgnoreCase) &&
            e.ChangedSections.Length > 0)
        {
            // 仅当变更列表为空（全量重载）或包含 Acp 时通知
            var relevant = e.ChangedSections.Length == 0
                || e.ChangedSections.Contains(AcpOptions.SectionName, StringComparer.OrdinalIgnoreCase);
            if (!relevant)
                return;
        }

        var value = CurrentValue;
        lock (_listeners)
        {
            foreach (var listener in _listeners)
            {
                try { listener(value, null); }
                catch { /* ignore listener failures */ }
            }
        }
    }

    private sealed class Disposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
