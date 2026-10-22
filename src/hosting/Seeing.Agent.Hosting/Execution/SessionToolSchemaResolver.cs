using System.Text.Json;
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

/// <summary>会话级 schema 结算内部结果：内联 schema + 全量 deferred + 显示分组 + 正文 + 度量。</summary>
internal sealed record SessionSchemaSettlement(
    IReadOnlyList<FunctionToolSchema> ToolSchemas,
    IReadOnlyList<string> SectionIds,
    IReadOnlyList<ToolCatalogEntry> DeferredEntries,
    IReadOnlyList<ToolCatalogNamespace> CatalogNamespaces,
    string? CatalogText,
    int InlineToolCount,
    int DeferredToolCount,
    int EstimatedDefinitionTokens,
    int CatalogTokens,
    ToolExposurePolicyMode ExposureMode);

/// <summary>
/// 会话级工具 schema 解析（<see cref="ISessionToolSchemaResolver"/> 唯一实现）：
/// 会话级结算 + 动态工具并入 + Agent 过滤 + 分节 id + 工具暴露裁剪（inline/目录分层 + auto 升级）。
/// <para>
/// schema 单点纪律：主执行链与旁路执行共用本结算；暴露裁剪、目录渲染、token 归属、发现态裁剪、
/// 升级粘滞——全部在此单点完成。单次执行内 schema 一经结算不再变（发现下一轮生效）。
/// 暴露子系统依赖经构造函数**强制注入**（缺失即构造失败，杜绝静默退化为全内联的接线漂移）。
/// </para>
/// </summary>
public sealed class SessionToolSchemaResolver : ISessionToolSchemaResolver
{
    private readonly IServiceProvider _rootProvider;
    private readonly IOptionsMonitor<SeeingAgentOptions> _seeingAgentOptions;
    private readonly ToolExposureConfigResolver _configResolver;
    private readonly ToolExposurePolicySelector _selector;
    private readonly ITokenCounter _tokenCounter;
    private readonly ILogger<SessionToolSchemaResolver> _logger;
    private readonly IScenarioCatalog? _scenarioCatalog;

    public SessionToolSchemaResolver(
        IServiceProvider rootProvider,
        IOptionsMonitor<SeeingAgentOptions> seeingAgentOptions,
        ToolExposureConfigResolver configResolver,
        ToolExposurePolicySelector policySelector,
        ITokenCounter tokenCounter,
        ILogger<SessionToolSchemaResolver>? logger = null,
        IScenarioCatalog? scenarioCatalog = null)
    {
        _rootProvider = rootProvider;
        _seeingAgentOptions = seeingAgentOptions;
        _configResolver = configResolver ?? throw new ArgumentNullException(nameof(configResolver));
        _selector = policySelector ?? throw new ArgumentNullException(nameof(policySelector));
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
        var session = sessionManager.Get(sessionId)
            ?? await sessionManager.LoadAsync(sessionId).ConfigureAwait(false)
            ?? await sessionManager.EnsureSessionAsync(sessionId).ConfigureAwait(false);

        var settlement = await SettleAsync(session, agent, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        return Map(settlement);
    }

    /// <inheritdoc />
    public async Task<SessionToolSchemaResolution> ResolveAsync(
        SessionData session,
        AgentDefinition agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);

        using var scope = _rootProvider.CreateScope();
        var settlement = await SettleAsync(session, agent, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        return Map(settlement);
    }

    private static SessionToolSchemaResolution Map(SessionSchemaSettlement s) => new(
        s.ToolSchemas,
        s.SectionIds,
        s.DeferredEntries,
        s.CatalogNamespaces,
        s.CatalogText,
        s.InlineToolCount,
        s.DeferredToolCount,
        s.EstimatedDefinitionTokens,
        s.CatalogTokens,
        s.ExposureMode);

    private async Task<SessionSchemaSettlement> SettleAsync(
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

            _logger.LogDebug(
                "会话级结算: session={SessionId}, scenario={Scenario}, modules={ModuleCount}, tools={ToolCount}",
                session.Id, settlement.ScenarioName, settlement.EnabledModules.Count, settlement.SettledToolIds.Count);
        }
        else
        {
            _logger.LogDebug("IModuleCatalog 未注册，跳过会话级结算（schema 空集）: {SessionId}", session.Id);
        }

        IReadOnlyList<FunctionToolSchema> toolSchemas = Array.Empty<FunctionToolSchema>();
        if (toolManager != null)
        {
            toolSchemas = await toolManager.GetToolSchemasAsync(settledToolIds, agent, cancellationToken).ConfigureAwait(false);
        }

        var sectionIds = services.GetServices<IPromptSectionContributor>()
            .Select(c => c.SectionName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 发现态裁剪的基准必须是**会话级**可用集（settledToolIds，未经 Agent Allowed/Denied 过滤），
        // 否则跨 Agent（如 build→plan）执行会把该 Agent 未允许、但会话仍可用的已发现工具误删。
        // 无 catalog 时 settledToolIds 为空且语义未知，传 null 表示"不裁剪"。
        IReadOnlySet<string>? sessionAvailable = settlement is not null
            ? new HashSet<string>(settledToolIds, StringComparer.OrdinalIgnoreCase)
            : null;
        return ApplyExposure(toolSchemas, sectionIds, session, agent, settlement?.ScenarioName,
            scenarioCatalog, sessionAvailable);
    }

    /// <summary>
    /// 工具暴露裁剪（单点）：四级有效模式 → auto 阈值升级(粘滞不回退) → 选择 All/Tiered 策略 →
    /// 全量 deferred（检索语料）+ 命名空间预算显示分组 + 正文渲染 → 度量与 token 写入会话缓存。
    /// </summary>
    private SessionSchemaSettlement ApplyExposure(
        IReadOnlyList<FunctionToolSchema> toolSchemas,
        IReadOnlyList<string> sectionIds,
        SessionData session,
        AgentDefinition agent,
        string? scenarioName,
        IScenarioCatalog? scenarioCatalog,
        IReadOnlySet<string>? sessionAvailableIds)
    {
        var scenarioDef = scenarioName is not null && scenarioCatalog is not null
            ? scenarioCatalog.Get(scenarioName)
            : null;
        var effective = _configResolver.Resolve(session.ScenarioOverride, agent, scenarioDef);

        // 单遍历：每 schema 估算一次 token，建 id→schema 与目录元信息。
        var schemaById = new Dictionary<string, FunctionToolSchema>(StringComparer.OrdinalIgnoreCase);
        var metas = new List<ToolMeta>(toolSchemas.Count);
        foreach (var s in toolSchemas)
        {
            var id = s.Function?.Name ?? string.Empty;
            var est = _tokenCounter.Estimate(JsonSerializer.Serialize(s));
            metas.Add(new ToolMeta(id, DeriveNamespace(s), s.Function?.Description ?? string.Empty, est));
            schemaById[id] = s;
        }

        var availableIds = metas.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 发现态：原子裁剪（会话级可用集）+ 加锁快照读取。
        // 仅当可确定会话级可用集时裁剪；保留 discovered ∩ 会话可用集，顺序不变。
        var discoveredSnapshot = session.GetDiscoveredToolsSnapshot().ToList();
        if (sessionAvailableIds is not null)
        {
            session.PruneDiscoveredTools(sessionAvailableIds);
            discoveredSnapshot = session.GetDiscoveredToolsSnapshot().ToList();
        }
        IReadOnlySet<string> discovered = new HashSet<string>(discoveredSnapshot, StringComparer.OrdinalIgnoreCase);

        // 升级判定（仅 auto）：阈值命中或已粘滞 → 进入分层；命中即置粘滞位，单调不回退。
        var escalated = session.ExposureEscalated;
        if (effective.Mode == ToolExposurePolicyMode.Auto)
        {
            escalated = ExposureEscalationTracker.ShouldEscalate(
                toolSchemas.Count, metas.Sum(m => m.EstimatedTokens), escalated,
                effective.EscalateToolCount, effective.EscalateSchemaTokens);
            if (escalated && !session.ExposureEscalated)
            {
                session.ExposureEscalated = true;
                _logger.LogDebug("auto 升级至分层: session={SessionId}", session.Id);
            }
        }

        var selectedMode = effective.Mode switch
        {
            ToolExposurePolicyMode.Tiered => ToolExposurePolicyMode.Tiered,
            ToolExposurePolicyMode.Auto => escalated ? ToolExposurePolicyMode.Tiered : ToolExposurePolicyMode.All,
            _ => ToolExposurePolicyMode.All
        };

        // 安全钳制：分层依赖检索入口。若本轮可用集不含 tool_search（可能是 tool-discovery 模块未挂载，
        // 也可能是 Agent 白名单/会话禁用排除），分层会把工具藏进目录却无检索手段 → 静默能力损伤。
        // 此时退回全内联（All）。用 Debug 避免无检索入口的宿主每轮刷 Warning。
        if (selectedMode == ToolExposurePolicyMode.Tiered
            && !availableIds.Contains(ToolIdConventions.SearchToolId))
        {
            _logger.LogDebug(
                "分层暴露生效但本轮可用集不含 {SearchTool}（模块未挂载/被白名单或禁用排除），降级为全内联: session={SessionId}",
                ToolIdConventions.SearchToolId, session.Id);
            selectedMode = ToolExposurePolicyMode.All;
        }

        var allEntries = ToolCatalogBuilder.Build(metas, effective.CatalogLineMaxChars);
        var input = new ToolExposureInput(effective.Mode, allEntries, discovered, effective.PinnedTools);
        var plan = _selector.Select(selectedMode).Plan(input);

        // 物化 inline schema，顺序：pinned(声明序) → 已发现(发现序) → 其余 inline(id 序)。
        var modeById = new Dictionary<string, ToolExposureMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in plan.CatalogEntries)
            modeById[e.Id] = e.Mode;

        var inlineIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void TryAdd(string id)
        {
            if (seen.Add(id) && modeById.TryGetValue(id, out var m)
                && m == ToolExposureMode.Inline && schemaById.ContainsKey(id))
                inlineIds.Add(id);
        }

        foreach (var p in effective.PinnedTools) TryAdd(p);
        foreach (var d in discoveredSnapshot) TryAdd(d);
        foreach (var e in plan.CatalogEntries) TryAdd(e.Id);

        var inlineSchemas = inlineIds.Select(id => schemaById[id]).ToList();

        // 全量 deferred = 检索语料；命名空间预算分组 = 显示（恒含每命名空间与总数，0-shown 仍出现）。
        var deferredEntries = plan.CatalogEntries
            .Where(e => e.Mode == ToolExposureMode.Deferred)
            .ToList();
        var namespaces = deferredEntries.Count > 0
            ? ToolCatalogBuilder.BuildNamespaces(deferredEntries, effective.CatalogBudgetChars)
            : Array.Empty<ToolCatalogNamespace>();
        var searchAvailable = availableIds.Contains(ToolIdConventions.SearchToolId);
        var catalogText = ToolCatalogRenderer.Render(namespaces, searchAvailable);
        var catalogTokens = namespaces.Sum(g => g.Shown.Sum(e => _tokenCounter.Estimate(e.Signature)));

        // token 归属单点写入会话缓存（与 ExposureEscalated 同类副作用；ExecutionRunner 与旁路均只读）。
        session.CachedSchemaTokens = plan.EstimatedDefinitionTokens;
        session.CachedCatalogTokens = catalogTokens;

        _logger.LogDebug(
            "工具暴露结算: session={SessionId}, mode={Mode}, inline={Inline}, deferred={Deferred}, schemaTokens={Schema}, catalogTokens={Catalog}",
            session.Id, selectedMode, plan.InlineToolCount, plan.DeferredToolCount, plan.EstimatedDefinitionTokens, catalogTokens);

        return new SessionSchemaSettlement(
            inlineSchemas, sectionIds, deferredEntries, namespaces, catalogText,
            plan.InlineToolCount, plan.DeferredToolCount, plan.EstimatedDefinitionTokens,
            catalogTokens, selectedMode);
    }

    /// <summary>从 schema 推导命名空间：MCP 取 <c>mcp__{server}</c> 前缀（唯一真相源为 id），内置归默认组（空）。</summary>
    private static string DeriveNamespace(FunctionToolSchema s)
    {
        var id = s.Function?.Name ?? string.Empty;
        if (id.StartsWith(ToolIdConventions.McpPrefix, StringComparison.Ordinal))
        {
            var rest = id.Substring(ToolIdConventions.McpPrefix.Length);
            var idx = rest.IndexOf(ToolIdConventions.McpSeparator, StringComparison.Ordinal);
            return idx >= 0 ? ToolIdConventions.McpPrefix + rest.Substring(0, idx) : id;
        }

        return string.Empty;
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
