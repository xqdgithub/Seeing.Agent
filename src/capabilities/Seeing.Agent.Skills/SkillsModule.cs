using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Skills.Commands;
using Seeing.Agent.Skills.Configuration;
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
        TryRegisterConfigSection(services);
        services.AddOptions<SkillsOptions>();
        services.TryAddSingleton(sp =>
            new ConfigSectionOptionsMonitor<SkillsOptions>(
                sp.GetRequiredService<IConfigSectionStore>(),
                SkillsOptions.SectionName));
        services.TryAddSingleton<IOptionsMonitor<SkillsOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<SkillsOptions>>());
        services.TryAddSingleton<IOptions<SkillsOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<SkillsOptions>>());

        services.AddSingleton<OnlineSkillParserAggregator>();

        services.AddSingleton<SkillManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SkillManager>>();
            var directories = sp.GetService<ISeeingDirectories>();
            return new SkillManager(logger, directories);
        });
        services.TryAddSingleton<ISkillManager>(sp => sp.GetRequiredService<SkillManager>());
        services.AddSingleton<IPromptSectionContributor>(sp => sp.GetRequiredService<SkillManager>());
        services.AddSingleton<SkillTool>();
        services.AddSingleton<IComponentLoader, SkillLoader>();

        // 静态斜杠命令（W1-4 InitializeCommands 扫描 GetServices<ICommand>）
        services.AddSingleton<ICommand, SkillLoadCommand>();
        services.AddSingleton<ICommand, SkillsListCommand>();
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/skills", "技能", "star", ["skills"],
            Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 10),
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ui?.Register(this);

        var tm = services.GetService<IToolManager>();
        var tool = services.GetService<SkillTool>();
        if (tm is not null && tool is not null)
            await tm.RegisterToolAsync(tool, cancellationToken).ConfigureAwait(false);

        // 静态命令亦经 registry 贡献（Activate 早于 InitializeCommands 时已可用）
        var registry = services.GetService<ICommandRegistry>();
        if (registry is not null)
        {
            foreach (var command in services.GetServices<ICommand>())
            {
                if (command is SkillLoadCommand or SkillsListCommand)
                    registry.Register(command);
            }
        }
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is not null)
        {
            foreach (var id in ProvidedTools)
                await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
        }

        var registry = services.GetService<ICommandRegistry>();
        if (registry is not null)
        {
            registry.Unregister("skill");
            registry.Unregister("skills");
            var skillManager = services.GetService<ISkillManager>();
            if (skillManager is not null)
            {
                foreach (var name in skillManager.GetAllSkillInfos().Keys)
                    registry.Unregister(name);
            }
        }

        _ui?.Unregister(Id);
    }

    private static void TryRegisterConfigSection(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigSectionRegistry) &&
                descriptor.ImplementationInstance is IConfigSectionRegistry registry)
            {
                registry.Register(new ConfigSectionMeta(
                    SkillsOptions.SectionName,
                    "seeing.json",
                    ConfigScope.Both,
                    typeof(SkillsOptions)));
                return;
            }
        }
    }
}
