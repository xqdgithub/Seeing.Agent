using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Skills.OnlineParsers;

namespace Seeing.Agent.Skills;

/// <summary>
/// 技能模块 — 提供 SkillManager、在线解析器与 skill 工具。
/// </summary>
public sealed class SkillsModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedTools = ["skill"];

    private static readonly IReadOnlyList<string> s_providedSeams = ["skills"];

    private readonly IUiContributionRegistry? _ui;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public SkillsModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public SkillsModule(IUiContributionRegistry? uiRegistry)
    {
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "skills";

    /// <inheritdoc />
    public string ModuleId => Id;

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
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/skills", "技能", "star", ["skills"]),
    ];

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ui?.Register(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        _ui?.Unregister(Id);
        return Task.CompletedTask;
    }
}
