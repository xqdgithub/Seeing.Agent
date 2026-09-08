using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Web;

/// <summary>
/// 网络工具模块 — 提供 webfetch/websearch/codesearch。
/// </summary>
public sealed class WebModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "webfetch",
        "websearch",
        "codesearch",
    ];

    /// <inheritdoc />
    public string Id => "web";

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
        services.AddSingleton<ITool>(sp => new WebFetchTool(
            sp.GetRequiredService<ILogger<WebFetchTool>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
        services.AddSingleton<ITool>(sp => new WebSearchTool(
            sp.GetRequiredService<ILogger<WebSearchTool>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
        services.AddSingleton<ITool>(sp => new CodeSearchTool(
            sp.GetRequiredService<ILogger<CodeSearchTool>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
