using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.SystemOne.Skills;

namespace Seeing.Agent.Core.Tools.SystemOne;

/// <summary>
/// SystemOne 判别工具模块 — 提供 <c>systemone_ask</c> 与内嵌 Skill。
/// <para>只依赖 Abstractions + Tools.Support；不引用 Core，也不引用 SystemOne 实现包。</para>
/// </summary>
public sealed class SystemOneToolsModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools = ["systemone_ask"];

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
        services.AddSingleton<SystemOneAskTool>();
        services.AddModuleHostedService<SystemOneSkillRegistrationHostedService>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        await tm.RegisterToolAsync(
            services.GetRequiredService<SystemOneAskTool>(), cancellationToken).ConfigureAwait(false);
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
