using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>Scheduler 配置提供者接口</summary>
public interface ISchedulerOptionsProvider
{
    /// <summary>当前配置</summary>
    SchedulerOptions Current { get; }
    
    /// <summary>重载配置</summary>
    void Reload();
}

/// <summary>
/// Scheduler 配置提供者 - 从 UnifiedConfigManager 获取配置
/// </summary>
public sealed class SchedulerOptionsProvider : ISchedulerOptionsProvider
{
    private readonly UnifiedConfigManager _configManager;
    private readonly ILogger<SchedulerOptionsProvider> _logger;
    private SchedulerOptions _options = new();

    public SchedulerOptionsProvider(
        UnifiedConfigManager configManager,
        ILogger<SchedulerOptionsProvider> logger)
    {
        _configManager = configManager;
        _logger = logger;
        
        // 订阅配置变更
        _configManager.ConfigChanged += OnConfigChanged;
    }

    /// <summary>当前配置</summary>
    public SchedulerOptions Current => _options;

    /// <summary>重载配置</summary>
    public void Reload()
    {
        _options = _configManager.GetSection<SchedulerOptions>("Scheduler") ?? new SchedulerOptions();
        _logger.LogDebug("Scheduler options reloaded (Enabled={Enabled}, Heartbeat={HeartbeatEnabled})",
            _options.Enabled, _options.Heartbeat.Enabled);
    }

    /// <summary>保存配置</summary>
    public async Task SaveAsync(SchedulerOptions options, CancellationToken ct = default)
    {
        await _configManager.SaveSectionAsync("Scheduler", options, ConfigLevel.Project, ct);
        Reload();
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.ContainsSection("Scheduler"))
        {
            Reload();
        }
    }
}