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

    /// <summary>
    /// 订阅配置变更（供 <see cref="IOptionsMonitor{SchedulerOptions}"/> 桥接）。
    /// <para>
    /// 返回的 <see cref="IDisposable"/> 释放时退订。实现方必须在 <see cref="Reload"/> 或
    /// <see cref="SchedulerOptionsProvider.SaveAsync"/> 之后触发回调——否则订阅
    /// <c>IOptionsMonitor&lt;SchedulerOptions&gt;</c> 的消费者永远收不到热重载通知
    /// （历史缺陷：适配器 <c>OnChange</c> 恒返回 null，改 scheduler.json 后运行中的调度参数不生效）。
    /// </para>
    /// </summary>
    IDisposable OnChange(Action<SchedulerOptions> listener);
}

/// <summary>
/// Scheduler 配置提供者 - 从 <see cref="IConfigSectionStore"/> 获取配置
/// </summary>
public sealed class SchedulerOptionsProvider : ISchedulerOptionsProvider
{
    private readonly IConfigSectionStore _configStore;
    private readonly ILogger<SchedulerOptionsProvider> _logger;
    private readonly object _gate = new();
    private readonly List<Action<SchedulerOptions>> _listeners = [];
    private SchedulerOptions _options = new();

    public SchedulerOptionsProvider(
        IConfigSectionStore configStore,
        ILogger<SchedulerOptionsProvider> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>当前配置</summary>
    public SchedulerOptions Current => Volatile.Read(ref _options);

    /// <inheritdoc />
    public IDisposable OnChange(Action<SchedulerOptions> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_gate)
            _listeners.Add(listener);

        return new Subscription(this, listener);
    }

    /// <summary>重载配置</summary>
    public void Reload()
    {
        var reloaded = _configStore.GetSection<SchedulerOptions>("Scheduler") ?? new SchedulerOptions();
        Volatile.Write(ref _options, reloaded);
        _logger.LogDebug("Scheduler options reloaded (Enabled={Enabled})", reloaded.Enabled);

        // 在锁内复制监听器、锁外调用：慢监听器不得阻塞后续订阅/退订，监听器抛错也不得中断其它监听器。
        Action<SchedulerOptions>[] snapshot;
        lock (_gate)
            snapshot = _listeners.ToArray();

        foreach (var listener in snapshot)
        {
            try
            {
                listener(reloaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler options 变更监听器抛异常（已隔离，继续通知其余监听器）");
            }
        }
    }

    /// <summary>保存配置</summary>
    public async Task SaveAsync(SchedulerOptions options, CancellationToken ct = default)
    {
        await _configStore.SaveSectionAsync("Scheduler", options, ConfigLevel.Project, ct);
        Reload();
    }

    private void Remove(Action<SchedulerOptions> listener)
    {
        lock (_gate)
            _listeners.Remove(listener);
    }

    private sealed class Subscription(
        SchedulerOptionsProvider owner,
        Action<SchedulerOptions> listener) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            owner.Remove(listener);
        }
    }
}
