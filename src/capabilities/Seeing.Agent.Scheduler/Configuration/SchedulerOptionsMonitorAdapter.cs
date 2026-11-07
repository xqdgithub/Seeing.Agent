using Microsoft.Extensions.Options;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>
/// 把 <see cref="ISchedulerOptionsProvider"/> 桥接为 <see cref="IOptionsMonitor{SchedulerOptions}"/>。
/// <para>
/// <see cref="OnChange"/> 必须真正转发到 provider 的变更通知：此前恒定返回 <c>null</c>，
/// 导致任何订阅 <c>IOptionsMonitor&lt;SchedulerOptions&gt;</c> 的消费者收不到热重载，
/// 改 <c>scheduler.json</c> 后运行中的调度参数静默不生效且无任何告警。
/// </para>
/// <para>命名选项（<c>Get(name)</c>）不支持按名分流：Scheduler 只有一个配置节，所有名字返回同一实例。</para>
/// </summary>
internal sealed class SchedulerOptionsMonitorAdapter : IOptions<SchedulerOptions>, IOptionsMonitor<SchedulerOptions>
{
    private readonly ISchedulerOptionsProvider _provider;

    public SchedulerOptionsMonitorAdapter(ISchedulerOptionsProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public SchedulerOptions Value => CurrentValue;

    public SchedulerOptions CurrentValue => _provider.Current;

    public SchedulerOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<SchedulerOptions, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        return _provider.OnChange(options => listener(options, Options.DefaultName));
    }
}
