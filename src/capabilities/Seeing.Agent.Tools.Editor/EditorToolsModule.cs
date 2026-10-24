using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Editor.Skills;

namespace Seeing.Agent.Tools.Editor;

/// <summary>
/// 编辑器工具模块 — 提供 editor_command / editor_query / editor_list_commands，
/// 并注册内嵌 editor-commands 技能。
/// <para>只依赖 Abstractions + Tools.Support；不引用 Core。</para>
/// </summary>
public sealed class EditorToolsModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
        ["editor_command", "editor_query", "editor_list_commands"];

    private readonly List<string> _registeredSkillNames = new();

    /// <inheritdoc />
    public string Id => "editor.tools";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<EditorCommandTool>();
        services.AddSingleton<EditorQueryTool>();
        services.AddSingleton<EditorListCommandsTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await RegisterEmbeddedSkillsAsync(services, cancellationToken).ConfigureAwait(false);

        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        await tm.RegisterToolAsync(
            services.GetRequiredService<EditorCommandTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<EditorQueryTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(
            services.GetRequiredService<EditorListCommandsTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await UnregisterEmbeddedSkillsAsync(services, cancellationToken).ConfigureAwait(false);

        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task RegisterEmbeddedSkillsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<ISkillCatalog>() is not { } catalog)
            return;

        var logger = services.GetService<ILogger<EditorToolsModule>>()
                     ?? NullLogger<EditorToolsModule>.Instance;
        _registeredSkillNames.Clear();
        _registeredSkillNames.AddRange(
            await EditorSkillRegistrar.RegisterAsync(catalog, logger, cancellationToken)
                .ConfigureAwait(false));
    }

    private async Task UnregisterEmbeddedSkillsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (_registeredSkillNames.Count == 0)
            return;

        if (services.GetService<ISkillCatalog>() is { } catalog)
        {
            var logger = services.GetService<ILogger<EditorToolsModule>>()
                         ?? NullLogger<EditorToolsModule>.Instance;
            await EditorSkillRegistrar
                .UnregisterAsync(catalog, _registeredSkillNames, logger, cancellationToken)
                .ConfigureAwait(false);
        }
        _registeredSkillNames.Clear();
    }
}
