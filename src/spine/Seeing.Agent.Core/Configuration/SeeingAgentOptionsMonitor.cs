using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// SeeingAgentOptions 的 IOptionsMonitor 实现 - 从 UnifiedConfigManager 获取配置，支持热重载
/// </summary>
public sealed class SeeingAgentOptionsMonitor : IOptions<SeeingAgentOptions>, IOptionsMonitor<SeeingAgentOptions>
{
    private readonly UnifiedConfigManager _manager;
    private readonly List<Action<SeeingAgentOptions, string?>> _changeListeners = new();

    /// <summary>初始化配置监视器，订阅统一配置管理器的变更事件。</summary>
    public SeeingAgentOptionsMonitor(UnifiedConfigManager manager)
    {
        _manager = manager;
        
        // 订阅配置变更事件
        _manager.ConfigChanged += OnConfigChanged;
    }

    // IOptions<SeeingAgentOptions>
    /// <summary>获取当前 SeeingAgentOptions 实例（IOptions 实现）。</summary>
    public SeeingAgentOptions Value => CurrentValue;

    // IOptionsMonitor<SeeingAgentOptions>
    /// <summary>从统一配置管理器获取最新配置（IOptionsMonitor 实现）。</summary>
    public SeeingAgentOptions CurrentValue => _manager.GetSeeingAgentOptions();

    /// <summary>按名称获取配置（忽略名称，始终返回当前值）。</summary>
    public SeeingAgentOptions Get(string? name) => CurrentValue;

    /// <summary>注册配置变更回调，返回可注销监听的可释放对象。</summary>
    public IDisposable? OnChange(Action<SeeingAgentOptions, string?> listener)
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
                    if (newValue is not null)
                        listener(newValue, null);
                }
                catch
                {
                }
            }
        }
    }

    private sealed class ChangeListenerDisposable : IDisposable
    {
        private readonly SeeingAgentOptionsMonitor _monitor;
        private readonly Action<SeeingAgentOptions, string?> _listener;

        public ChangeListenerDisposable(SeeingAgentOptionsMonitor monitor, Action<SeeingAgentOptions, string?> listener)
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
