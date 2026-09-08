using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
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
/// Scheduler 配置提供者 - 从 <see cref="IConfigSectionStore"/> 获取配置
/// </summary>
public sealed class SchedulerOptionsProvider : ISchedulerOptionsProvider
{
    private readonly IConfigSectionStore _configStore;
    private readonly ILogger<SchedulerOptionsProvider> _logger;
    private SchedulerOptions _options = new();

    public SchedulerOptionsProvider(
        IConfigSectionStore configStore,
        ILogger<SchedulerOptionsProvider> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>当前配置</summary>
    public SchedulerOptions Current => _options;

    /// <summary>重载配置</summary>
    public void Reload()
    {
        _options = _configStore.GetSection<SchedulerOptions>("Scheduler") ?? new SchedulerOptions();
        _logger.LogDebug("Scheduler options reloaded (Enabled={Enabled}, Heartbeat={HeartbeatEnabled})",
            _options.Enabled, _options.Heartbeat.Enabled);
    }

    /// <summary>保存配置</summary>
    public async Task SaveAsync(SchedulerOptions options, CancellationToken ct = default)
    {
        await _configStore.SaveSectionAsync("Scheduler", options, ConfigLevel.Project, ct);
        Reload();
    }
}
