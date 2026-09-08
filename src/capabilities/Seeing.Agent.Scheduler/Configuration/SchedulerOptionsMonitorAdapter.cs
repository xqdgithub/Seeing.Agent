using Microsoft.Extensions.Options;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>
/// Bridges <see cref="ISchedulerOptionsProvider"/> to <see cref="IOptionsMonitor{SchedulerOptions}"/>.
/// </summary>
internal sealed class SchedulerOptionsMonitorAdapter : IOptions<SchedulerOptions>, IOptionsMonitor<SchedulerOptions>
{
    private readonly ISchedulerOptionsProvider _provider;

    public SchedulerOptionsMonitorAdapter(ISchedulerOptionsProvider provider)
    {
        _provider = provider;
    }

    public SchedulerOptions Value => CurrentValue;

    public SchedulerOptions CurrentValue => _provider.Current;

    public SchedulerOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<SchedulerOptions, string?> listener) => null;
}
