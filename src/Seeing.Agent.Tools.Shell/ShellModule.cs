using Microsoft.Extensions.DependencyInjection;
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
        // Interim: register ITool + IShellService so existing ToolManager discovery still works
        // without Activate until Host Shape lands.
        services.AddSingleton<IShellService, DefaultShellService>();
        services.AddSingleton<ITool, BashTool>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
