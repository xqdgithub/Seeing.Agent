using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Hosting;

namespace Seeing.Agent.Scheduler;

/// <summary>
/// Scheduler 能力模块 — id=<c>scheduler</c>。
/// </summary>
public sealed class SchedulerModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "cron_list",
        "cron_create",
        "cron_delete",
        "cron_disable",
        "cron_resume",
        "cron_run",
    ];

    private readonly SchedulerModuleActivity? _activity;
    private readonly IScheduleManager? _manager;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public SchedulerModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public SchedulerModule(SchedulerModuleActivity activity, IScheduleManager manager)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <inheritdoc />
    public string Id => "scheduler";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 实际 DI 登记由 AddSeeingScheduler 完成。
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activity is null)
        {
            throw new InvalidOperationException(
                "SchedulerModule.ActivateAsync requires DI-resolved SchedulerModule.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _activity.MarkActive();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activity is null)
            return;

        _activity.MarkInactiveAndWake();
        if (_manager is not null)
            await _manager.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
