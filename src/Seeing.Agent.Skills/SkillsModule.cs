using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Skills.OnlineParsers;

namespace Seeing.Agent.Skills;

/// <summary>
/// 技能模块 — 提供 SkillManager、在线解析器与 skill 工具。
/// </summary>
public sealed class SkillsModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools = ["skill"];

    private static readonly IReadOnlyList<string> s_providedSeams = ["skills"];

    /// <inheritdoc />
    public string Id => "skills";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => s_providedSeams;

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // Interim: register SkillManager + parsers + skill tool so existing discovery still works
        // without Activate until Host Shape lands.
        services.AddSingleton<OnlineSkillParserAggregator>();

        services.AddSingleton<SkillManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SkillManager>>();
            var directories = sp.GetService<ISeeingDirectories>();
            return new SkillManager(logger, directories);
        });
        services.TryAddSingleton<ISkillManager>(sp => sp.GetRequiredService<SkillManager>());
        services.AddSingleton<IPromptSectionContributor>(sp => sp.GetRequiredService<SkillManager>());
        services.AddSingleton<ITool, SkillTool>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
