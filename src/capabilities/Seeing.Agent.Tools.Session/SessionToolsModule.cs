using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Session;

/// <summary>
/// 会话工具模块 — 提供 session_list/search/read/trim/handoff/inbox_send/takeover/release。
/// <para>只依赖 Abstractions + Seeing.Session + Tools.Support；不引用 Core。</para>
/// </summary>
public sealed class SessionToolsModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "session_list",
        "session_search",
        "session_read",
        "session_trim",
        "session_handoff",
        "session_inbox_send",
        "session_takeover",
        "session_release",
    ];

    /// <inheritdoc />
    public string Id => "session.tools";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SessionListTool>();
        services.AddSingleton<SessionSearchTool>();
        services.AddSingleton<SessionReadTool>();
        services.AddSingleton<SessionTrimTool>();
        services.AddSingleton<SessionHandoffTool>();
        services.AddSingleton<SessionInboxSendTool>();
        services.AddSingleton<SessionTakeoverTool>();
        services.AddSingleton<SessionReleaseTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        await tm.RegisterToolAsync(
            services.GetRequiredService<SessionListTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<SessionSearchTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<SessionReadTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<SessionTrimTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<SessionTakeoverTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<SessionReleaseTool>(), cancellationToken).ConfigureAwait(false);

        if (services.GetService<ISessionRuntime>() is not null)
        {
            await tm.RegisterToolAsync(
                services.GetRequiredService<SessionHandoffTool>(), cancellationToken).ConfigureAwait(false);
            await tm.RegisterToolAsync(
                services.GetRequiredService<SessionInboxSendTool>(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            services.GetService<ILogger<SessionToolsModule>>()?.LogWarning(
                "ISessionRuntime 未注册，session_handoff / session_inbox_send 未挂载");
        }
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
