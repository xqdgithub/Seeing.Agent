using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Core.Tools.Shell;

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
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetRequiredService<IToolManager>();
        await tm.RegisterToolAsync(services.GetRequiredService<BashTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetRequiredService<IToolManager>();
        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
