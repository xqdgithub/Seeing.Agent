using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Basic;

/// <summary>
/// 基础工具模块 — 提供 current_time。
/// </summary>
public sealed class BasicModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools = ["current_time"];

    /// <inheritdoc />
    public string Id => "basic";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // Interim: register ITool implementations so existing ToolManager discovery still works
        // without Activate until Host Shape lands.
        services.AddSingleton<ITool, CurrentTimeTool>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
