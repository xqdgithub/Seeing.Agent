using Microsoft.Extensions.Options;

namespace Seeing.Agent.Configuration;

/// <summary>
/// SeeingAgentOptions 的 IOptionsMonitor 实现 - 从 UnifiedConfigManager 获取配置，支持热重载
/// </summary>
public sealed class SeeingAgentOptionsMonitor : IOptions<SeeingAgentOptions>, IOptionsMonitor<SeeingAgentOptions>
{
    private readonly UnifiedConfigManager _manager;
    private readonly List<Action<SeeingAgentOptions?, string?>> _changeListeners = new();

    public SeeingAgentOptionsMonitor(UnifiedConfigManager manager)
    {
        _manager = manager;
        
        // 订阅配置变更事件
        _manager.ConfigChanged += OnConfigChanged;
    }

    // IOptions<SeeingAgentOptions>
    public SeeingAgentOptions Value => CurrentValue;

    // IOptionsMonitor<SeeingAgentOptions>
    public SeeingAgentOptions CurrentValue => _manager.GetSeeingAgentOptions();

    public SeeingAgentOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<SeeingAgentOptions?, string?> listener)
    {
        lock (_changeListeners)
        {
            _changeListeners.Add(listener);
        }

        return new ChangeListenerDisposable(this, listener);
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // 配置变更时通知所有监听器
        SeeingAgentOptions? newValue;
        try
        {
            newValue = CurrentValue;
        }
        catch
        {
            newValue = null;
        }

        lock (_changeListeners)
        {
            foreach (var listener in _changeListeners)
            {
                try
                {
                    listener(newValue, null);
                }
                catch
                {
                    // 忽略监听器异常
                }
            }
        }
    }

    private sealed class ChangeListenerDisposable : IDisposable
    {
        private readonly SeeingAgentOptionsMonitor _monitor;
        private readonly Action<SeeingAgentOptions?, string?> _listener;

        public ChangeListenerDisposable(SeeingAgentOptionsMonitor monitor, Action<SeeingAgentOptions?, string?> listener)
        {
            _monitor = monitor;
            _listener = listener;
        }

        public void Dispose()
        {
            lock (_monitor._changeListeners)
            {
                _monitor._changeListeners.Remove(_listener);
            }
        }
    }
}
