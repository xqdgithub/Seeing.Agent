using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 会话级工具视图解析（<see cref="ISessionToolSchemaResolver"/> 唯一实现）。
/// <para>
/// 结算顺序：会话级结算（scenario / 模块启用 / 工具禁用）→ 动态工具并入 → Agent 过滤 →
/// 暴露裁剪（纯引擎）→ 分节 id 收集。
/// </para>
/// <para>
/// **本类型只读**：不写会话状态、不创建会话。状态提交由执行入口在每轮开头完成。
/// </para>
/// </summary>
public sealed class SessionToolSchemaResolver : ISessionToolSchemaResolver
{
    private readonly IServiceProvider _rootProvider;
    private readonly IOptionsMonitor<SeeingAgentOptions> _seeingAgentOptions;
    private readonly ToolExposureConfigResolver _configResolver;
    private readonly ITokenCounter _tokenCounter;
    private readonly ILogger<SessionToolSchemaResolver> _logger;
    private readonly IScenarioCatalog? _scenarioCatalog;

    public SessionToolSchemaResolver(
        IServiceProvider rootProvider,
        IOptionsMonitor<SeeingAgentOptions> seeingAgentOptions,
        ToolExposureConfigResolver configResolver,
        ITokenCounter tokenCounter,
        ILogger<SessionToolSchemaResolver>? logger = null,
        IScenarioCatalog? scenarioCatalog = null)
    {
        _rootProvider = rootProvider;
        _seeingAgentOptions = seeingAgentOptions;
        _configResolver = configResolver ?? throw new ArgumentNullException(nameof(configResolver));
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionToolSchemaResolver>.Instance;
        _scenarioCatalog = scenarioCatalog;
    }

    /// <inheritdoc />
    public async Task<SessionToolSchemaResolution> ResolveAsync(
        string sessionId,
        AgentDefinition agent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || agent is null)
            return SessionToolSchemaResolution.Empty;

        using var scope = _rootProvider.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
        // 只读：会话不存在时返回空视图，绝不新建会话。
        var session = sessionManager.Get(sessionId)
            ?? await sessionManager.LoadAsync(sessionId).ConfigureAwait(false);
        if (session is null)
            return SessionToolSchemaResolution.Empty;

        return await SettleAsync(session, agent, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SessionToolSchemaResolution> ResolveAsync(
        SessionData session,
        AgentDefinition agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);

        using var scope = _rootProvider.CreateScope();
        return SettleAsync(session, agent, scope.ServiceProvider, cancellationToken);
    }

    private async Task<SessionToolSchemaResolution> SettleAsync(
        SessionData session,
        AgentDefinition agent,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var catalog = services.GetService<IModuleCatalog>();
        var toolManager = services.GetService<IToolManager>();
        var options = _seeingAgentOptions.CurrentValue;
        var processScenario = options.Scenario
            ?? services.GetService<ProcessSettlementOptions>()?.HostDefaultScenario;
        var scenarioCatalog = _scenarioCatalog ?? services.GetService<IScenarioCatalog>();

        SessionSettlementSnapshot? settlement = null;
        IReadOnlyList<string> settledToolIds = Array.Empty<string>();
        IReadOnlySet<string>? availableIds = null;

        if (catalog != null)
        {
            settlement = SessionSettlement.Compute(
                session,
                catalog,
                processScenario,
                options.Modules?.Tools?.Disabled,
                resolveScenario: scenarioCatalog is not null
                    ? name => scenarioCatalog.Get(name)
                    : null);
            settledToolIds = settlement.SettledToolIds;

            var dynamicRegistry = services.GetService<IDynamicToolContributorRegistry>();
            if (dynamicRegistry is not null)
            {
                var dynamicIds = dynamicRegistry.GetDynamicToolIds(settlement.EnabledModules);
                if (dynamicIds.Count > 0)
                    settledToolIds = MergeDynamicToolIds(settledToolIds, dynamicIds);
            }

            // 会话级可用集：未经 Agent 过滤，作为发现集裁剪基准。
            availableIds = new HashSet<string>(settledToolIds, StringComparer.OrdinalIgnoreCase);

            _logger.LogDebug(
                "会话级结算: session={SessionId}, scenario={Scenario}, modules={ModuleCount}, tools={ToolCount}",
                session.Id, settlement.ScenarioName, settlement.EnabledModules.Count, settledToolIds.Count);
        }
        else
        {
            _logger.LogDebug("IModuleCatalog 未注册，跳过会话级结算（工具视图为空）: {SessionId}", session.Id);
        }

        IReadOnlyList<FunctionToolSchema> candidates = Array.Empty<FunctionToolSchema>();
        if (toolManager != null)
        {
            candidates = await toolManager.GetToolSchemasAsync(settledToolIds, agent, cancellationToken)
                .ConfigureAwait(false);
        }

        var sectionIds = services.GetServices<IPromptSectionContributor>()
            .Select(c => c.SectionName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var scenarioDef = settlement?.ScenarioName is { } name && scenarioCatalog is not null
            ? scenarioCatalog.Get(name)
            : null;
        var effective = _configResolver.Resolve(session.ScenarioOverride, agent, scenarioDef);

        var result = ToolExposureEngine.Compute(
            new ToolExposureInput(
                candidates,
                availableIds,
                session.ToolExposure.GetDiscovered(),
                session.ToolExposure.Escalated,
                effective),
            _tokenCounter);

        _logger.LogDebug(
            "工具暴露结算: session={SessionId}, layout={Layout}, inline={Inline}, deferred={Deferred}, schemaTokens={Schema}, catalogTokens={Catalog}",
            session.Id, result.Layout, result.Tools.Count(t => t.Mode == ToolExposureMode.Inline),
            result.Tools.Count(t => t.Mode == ToolExposureMode.Deferred), result.SchemaTokens, result.CatalogTokens);

        return new SessionToolSchemaResolution(
            result.Tools,
            sectionIds,
            result.Layout,
            result.CatalogText,
            result.SchemaTokens,
            result.CatalogTokens,
            availableIds);
    }

    internal static IReadOnlyList<string> MergeDynamicToolIds(
        IReadOnlyList<string> settledToolIds,
        IEnumerable<string> dynamicToolIds)
    {
        ArgumentNullException.ThrowIfNull(settledToolIds);
        ArgumentNullException.ThrowIfNull(dynamicToolIds);

        var merged = new HashSet<string>(settledToolIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in dynamicToolIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
                merged.Add(id.Trim());
        }

        return merged.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
