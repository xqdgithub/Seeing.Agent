using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.SystemOne.Skills;

namespace Seeing.Agent.Core.Tools.SystemOne;

/// <summary>
/// SystemOne 判别工具模块 — 提供 systemone_ask（混合）+ systemone_noul/choice/score（同类）与内嵌 Skill。
/// <para>只依赖 Abstractions + Tools.Support。</para>
/// </summary>
public sealed class SystemOneToolsModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
        ["systemone_ask", "systemone_noul", "systemone_choice", "systemone_score"];

    /// <inheritdoc />
    public string Id => "systemone.tools";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = new[] { "systemone" };

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        foreach (var kind in Enum.GetValues<SystemOneToolKind>())
        {
            var captured = kind;
            services.AddSingleton(sp => new SystemOneTool(
                captured, sp.GetRequiredService<ILogger<SystemOneTool>>()));
        }

        services.AddModuleHostedService<SystemOneSkillRegistrationHostedService>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        foreach (var tool in services.GetServices<SystemOneTool>())
            await tm.RegisterToolAsync(tool, cancellationToken).ConfigureAwait(false);
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
