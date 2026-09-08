using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.TokenBudget.Configuration;

/// <summary>
/// 从 <see cref="IConfigSectionStore"/> 桥接 <see cref="TokenBudgetOptions"/>。
/// </summary>
public sealed class TokenBudgetOptionsMonitor : IOptions<TokenBudgetOptions>, IOptionsMonitor<TokenBudgetOptions>
{
    private readonly IConfigSectionStore _store;
    private readonly List<Action<TokenBudgetOptions, string?>> _listeners = new();

    public TokenBudgetOptionsMonitor(IConfigSectionStore store)
    {
        _store = store;
        _store.ConfigChanged += (_, _) =>
        {
            var value = CurrentValue;
            lock (_listeners)
            {
                foreach (var listener in _listeners)
                {
                    try { listener(value, null); }
                    catch { /* ignore */ }
                }
            }
        };
    }

    public TokenBudgetOptions Value => CurrentValue;

    public TokenBudgetOptions CurrentValue =>
        _store.GetSection<TokenBudgetOptions>(TokenBudgetOptions.SectionName);

    public TokenBudgetOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<TokenBudgetOptions, string?> listener)
    {
        lock (_listeners)
            _listeners.Add(listener);
        return new Disposable(() =>
        {
            lock (_listeners)
                _listeners.Remove(listener);
        });
    }

    private sealed class Disposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
