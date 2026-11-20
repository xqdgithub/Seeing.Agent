using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Background;

/// <summary>
/// 后台活动工具模块 — 提供 activity_status/output/wait/cancel/list。
/// <para>
/// 只依赖 Abstractions + Tools.Support；不引用 Core。
/// 工具只注入 <see cref="Seeing.Agent.Abstractions.Background.IBackgroundActivityManager"/>（会话面），
/// 系统面 <c>IBackgroundActivityControl</c> 不进工具依赖。
/// </para>
/// </summary>
public sealed class BackgroundToolsModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "activity_status",
        "activity_output",
        "activity_wait",
        "activity_cancel",
        "activity_list",
    ];

    /// <inheritdoc />
    public string Id => "background";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ActivityStatusTool>();
        services.AddSingleton<ActivityOutputTool>();
        services.AddSingleton<ActivityWaitTool>();
        services.AddSingleton<ActivityCancelTool>();
        services.AddSingleton<ActivityListTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        await tm.RegisterToolAsync(
            services.GetRequiredService<ActivityStatusTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<ActivityOutputTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<ActivityWaitTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<ActivityCancelTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<ActivityListTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
