using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Shell;

/// <summary>
/// Shell 工具模块 — 提供 bash 命令执行。
/// </summary>
public sealed class ShellModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools = ["bash"];

    private static readonly IReadOnlyList<string> s_dependsOn = ["io.local"];

    /// <inheritdoc />
    public string Id => "shell";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => s_dependsOn;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IShellService, DefaultShellService>();
        services.AddSingleton<BashTool>();
        services.AddSingleton<ProcessBackgroundActivityLauncher>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetRequiredService<IToolManager>();
        await tm.RegisterToolAsync(services.GetRequiredService<BashTool>(), cancellationToken).ConfigureAwait(false);

        // 后台活动系统面是可选依赖：宿主未接线时（无背景活动能力的宿主）跳过注册，不抛异常。
        var control = services.GetService<IBackgroundActivityControl>();
        if (control is not null)
        {
            control.RegisterLauncher(Id, services.GetRequiredService<ProcessBackgroundActivityLauncher>());
        }
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // 先注销 launcher：必须取消并等完该 kind 名下的在途进程，再摘工具。
        var control = services.GetService<IBackgroundActivityControl>();
        if (control is not null)
        {
            await control.UnregisterLauncherAsync(ProcessBackgroundActivityLauncher.ProcessKind, cancellationToken)
                .ConfigureAwait(false);
        }

        var tm = services.GetRequiredService<IToolManager>();
        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
