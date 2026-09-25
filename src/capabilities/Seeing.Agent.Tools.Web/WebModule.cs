using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Core.Tools.Web;

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

    private const string WebFetchClientName = "webfetch";

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
        // webfetch 禁用自动重定向：由 WebFetchTool 手工逐跳跟随并逐跳做 SSRF 校验
        services.AddHttpClient(WebFetchClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton(sp => new WebFetchTool(
            sp.GetRequiredService<ILogger<WebFetchTool>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebFetchClientName)));
        services.AddSingleton(sp => new WebSearchTool(
            sp.GetRequiredService<ILogger<WebSearchTool>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
        services.AddSingleton(sp => new CodeSearchTool(
            sp.GetRequiredService<ILogger<CodeSearchTool>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetRequiredService<IToolManager>();
        await tm.RegisterToolAsync(services.GetRequiredService<WebFetchTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<WebSearchTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<CodeSearchTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetRequiredService<IToolManager>();
        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
