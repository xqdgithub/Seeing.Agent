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

/// <summary>
/// 会话级 schema 结算内部结果：内联 schema + 目录条目 + 分节 id + 暴露度量。
/// </summary>
internal sealed record SessionSchemaSettlement(
    IReadOnlyList<FunctionToolSchema> ToolSchemas,
    IReadOnlyList<string> SectionIds,
    IReadOnlyList<ToolCatalogEntry> CatalogEntries,
    int InlineToolCount,
    int DeferredToolCount,
    int EstimatedDefinitionTokens,
    int CatalogTokens,
    ToolExposurePolicyMode ExposureMode);

/// <summary>
/// 会话级工具 schema 解析（<see cref="ISessionToolSchemaResolver"/> 唯一实现）：
/// 会话级结算（scenario / 模块启用 / 工具禁用）+ 动态工具贡献并入 + Agent 过滤 + 分节 id 收集
/// + 工具暴露裁剪（inline / 目录分层 + auto 升级判定）。
/// <para>
/// schema 单点纪律：主执行链（ExecutionRunner）与旁路执行（Scheduler AgentJob / HeartbeatJob
/// 直连 <see cref="Seeing.Agent.Abstractions.Agents.IAgentExecutor"/>）共用本结算，
/// 保证任何执行路径的 LLM 请求都携带真实工具 schema。暴露裁剪亦在此单点完成——
/// 单次执行内 schema 一经结算不再变更（发现于下一轮生效）。
/// </para>
/// </summary>
public sealed class SessionToolSchemaResolver : ISessionToolSchemaResolver
{
    private readonly IServiceProvider _rootProvider;
    private readonly IOptionsMonitor<SeeingAgentOptions> _seeingAgentOptions;
    private readonly ILogger<SessionToolSchemaResolver> _logger;
    private readonly IScenarioCatalog? _scenarioCatalog;

    public SessionToolSchemaResolver(
        IServiceProvider rootProvider,
        IOptionsMonitor<SeeingAgentOptions> seeingAgentOptions,
        ILogger<SessionToolSchemaResolver>? logger = null,
        IScenarioCatalog? scenarioCatalog = null)
    {
        _rootProvider = rootProvider;
        _seeingAgentOptions = seeingAgentOptions;
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

        // 复用调用方已加载的 session 实例；结算依赖均为单例，从根容器自开作用域与主执行链 turn 作用域等价，
        // 且避免二次加载读到未落盘前的旧状态。
        using var scope = _rootProvider.CreateScope();
        var settlement = await SettleAsync(session, agent, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        return Map(settlement);
    }

    private static SessionToolSchemaResolution Map(SessionSchemaSettlement s) => new(
        s.ToolSchemas,
        s.SectionIds,
        s.CatalogEntries,
        s.InlineToolCount,
        s.DeferredToolCount,
        s.EstimatedDefinitionTokens,
        s.CatalogTokens,
        s.ExposureMode);

    /// <summary>
    /// 会话级结算 + schema 唯一计算 + 暴露裁剪（本类私有核心，仅供两个公开端口重载调用）。
    /// 无 <see cref="IModuleCatalog"/> 时 settled 为空集（schema 亦空）。
    /// </summary>
    private async Task<SessionSchemaSettlement> SettleAsync(
        SessionData session,
        AgentDefinition agent,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
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

            // 动态工具贡献（如 MCP 运行时注册的工具）并入结算集，避免被 ToolManager.settled.Contains 滤出 schema。
            var dynamicRegistry = services.GetService<IDynamicToolContributorRegistry>();
            if (dynamicRegistry is not null)
            {
                var dynamicIds = dynamicRegistry.GetDynamicToolIds(settlement.EnabledModules);
                if (dynamicIds.Count > 0)
                    settledToolIds = MergeDynamicToolIds(settledToolIds, dynamicIds);
            }

            _logger.LogDebug(
                "会话级结算: session={SessionId}, scenario={Scenario}, modules={ModuleCount}, tools={ToolCount}",
                session.Id,
                settlement.ScenarioName,
                settlement.EnabledModules.Count,
                settlement.SettledToolIds.Count);
        }
        else
        {
            _logger.LogDebug(
                "IModuleCatalog 未注册，跳过会话级结算（schema 空集）: {SessionId}",
                session.Id);
        }

        IReadOnlyList<FunctionToolSchema> toolSchemas = Array.Empty<FunctionToolSchema>();
        if (toolManager != null)
        {
            toolSchemas = await toolManager.GetToolSchemasAsync(settledToolIds, agent, cancellationToken)
                .ConfigureAwait(false);
        }

        var sectionIds = services.GetServices<IPromptSectionContributor>()
            .Select(c => c.SectionName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ApplyExposure(toolSchemas, sectionIds, session, agent, services, settlement?.ScenarioName, scenarioCatalog);
    }

    /// <summary>
    /// 工具暴露裁剪（组12，单点）：依四级有效模式在 inline / 目录间分层；
    /// auto 按阈值 + 会话粘滞判定升级（升级=进入分层，单调不回退）；发现态裁剪消失项。
    /// 元信息全部来自<paramref name="toolSchemas"/>同一次遍历（不二次查注册表）。
    /// </summary>
    private SessionSchemaSettlement ApplyExposure(
        IReadOnlyList<FunctionToolSchema> toolSchemas,
        IReadOnlyList<string> sectionIds,
        SessionData session,
        AgentDefinition agent,
        IServiceProvider services,
        string? scenarioName,
        IScenarioCatalog? scenarioCatalog)
    {
        var counter = services.GetService<ITokenCounter>() ?? new CharBasedTokenCounter();
        var configResolver = services.GetService<ToolExposureConfigResolver>();
        var selector = services.GetService<ToolExposurePolicySelector>();
        var store = services.GetService<IToolDiscoveryStore>();

        // 暴露子系统未接线（缺配置/选择器）→ 退化为全内联（与改造前一致），不引入目录。
        if (configResolver is null || selector is null)
        {
            var tokens = toolSchemas.Sum(s => counter.Estimate(JsonSerializer.Serialize(s)));
            return new SessionSchemaSettlement(
                toolSchemas, sectionIds, Array.Empty<ToolCatalogEntry>(),
                toolSchemas.Count, 0, tokens, 0, ToolExposurePolicyMode.All);
        }

        var scenarioDef = scenarioName is not null && scenarioCatalog is not null
            ? scenarioCatalog.Get(scenarioName)
            : null;
        var effective = configResolver.Resolve(session.ScenarioOverride, agent, scenarioDef);

        // 单遍历：每 schema 估算一次 token，建 id→schema 与目录元信息。
        var schemaById = new Dictionary<string, FunctionToolSchema>(StringComparer.OrdinalIgnoreCase);
        var metas = new List<ToolMeta>(toolSchemas.Count);
        foreach (var s in toolSchemas)
        {
            var id = s.Function?.Name ?? string.Empty;
            var est = counter.Estimate(JsonSerializer.Serialize(s));
            var pinned = effective.PinnedTools.Any(p => string.Equals(p, id, StringComparison.Ordinal));
            metas.Add(new ToolMeta(id, DeriveNamespace(s), id, s.Function?.Description ?? string.Empty, est, pinned));
            schemaById[id] = s;
        }

        // 发现态：裁剪消失项后读取（工具被注销/禁用时不再内联）。
        var availableIds = metas.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> discovered;
        if (store is not null)
        {
            store.Prune(session.Id, availableIds);
            discovered = store.Get(session.Id);
        }
        else
        {
            discovered = new HashSet<string>(session.DiscoveredTools, StringComparer.OrdinalIgnoreCase);
        }

        // 升级判定（仅 auto）：阈值命中或已粘滞 → 进入分层；命中即置粘滞位，单调不回退。
        var escalated = session.ExposureEscalated;
        if (effective.Mode == ToolExposurePolicyMode.Auto)
        {
            escalated = ExposureEscalationTracker.ShouldEscalate(
                toolSchemas.Count,
                metas.Sum(m => m.EstimatedTokens),
                escalated,
                effective.EscalateToolCount,
                effective.EscalateSchemaTokens);
            if (escalated && !session.ExposureEscalated)
            {
                session.ExposureEscalated = true;
                _logger.LogDebug(
                    "auto 升级至分层: session={SessionId}, tools={ToolCount}, tokens={Tokens}",
                    session.Id, toolSchemas.Count, metas.Sum(m => m.EstimatedTokens));
            }
        }

        // 选择生效策略：auto→(escalated?Tiered:All)；all/tiered 直接用其本身。
        var selectedMode = effective.Mode switch
        {
            ToolExposurePolicyMode.Tiered => ToolExposurePolicyMode.Tiered,
            ToolExposurePolicyMode.Auto => escalated
                ? ToolExposurePolicyMode.Tiered
                : ToolExposurePolicyMode.All,
            _ => ToolExposurePolicyMode.All
        };

        var allEntries = ToolCatalogBuilder.Build(metas, effective.CatalogLineMaxChars);
        var input = new ToolExposureInput(
            effective.Mode,
            allEntries,
            discovered,
            effective.PinnedTools,
            effective.CatalogBudgetChars,
            effective.CatalogLineMaxChars,
            escalated);

        var plan = selector.Select(selectedMode).Plan(input);

        // 物化 inline schema，顺序：pinned(声明序) → 已发现(发现序) → 其余 inline(id 序，allEntries 已 (ns,id) 排)。
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
        foreach (var d in session.DiscoveredTools) TryAdd(d);
        foreach (var e in plan.CatalogEntries) TryAdd(e.Id);

        var inlineSchemas = inlineIds.Select(id => schemaById[id]).ToList();

        // 目录（display）：deferred 条目按命名空间预算裁剪，未完整展示的标注计数。
        var deferredMetas = plan.CatalogEntries
            .Where(e => e.Mode == ToolExposureMode.Deferred)
            .Select(e => new ToolMeta(e.Id, e.Namespace, e.Id, e.Summary, e.EstimatedTokens, false))
            .ToList();
        IReadOnlyList<ToolCatalogEntry> catalogEntries = deferredMetas.Count > 0
            ? ToolCatalogBuilder.BuildWithinBudget(deferredMetas, effective.CatalogBudgetChars, effective.CatalogLineMaxChars)
            : Array.Empty<ToolCatalogEntry>();

        var catalogTokens = catalogEntries.Sum(e => counter.Estimate(e.Signature));

        _logger.LogDebug(
            "工具暴露结算: session={SessionId}, mode={Mode}, inline={Inline}, deferred={Deferred}, schemaTokens={Schema}, catalogTokens={Catalog}",
            session.Id, selectedMode, plan.InlineToolCount, plan.DeferredToolCount,
            plan.EstimatedDefinitionTokens, catalogTokens);

        return new SessionSchemaSettlement(
            inlineSchemas,
            sectionIds,
            catalogEntries,
            plan.InlineToolCount,
            plan.DeferredToolCount,
            plan.EstimatedDefinitionTokens,
            catalogTokens,
            selectedMode);
    }

    /// <summary>
    /// 从 schema 推导命名空间：MCP 工具取 <c>mcp__{server}</c> 前缀，内置工具归默认组（空）。
    /// </summary>
    private static string DeriveNamespace(FunctionToolSchema s)
    {
        var id = s.Function?.Name ?? string.Empty;
        if (id.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var rest = id.Substring("mcp__".Length);
            var idx = rest.IndexOf("__", StringComparison.Ordinal);
            return idx >= 0 ? "mcp__" + rest.Substring(0, idx) : id;
        }

        if (!string.IsNullOrEmpty(s.Function?.ServerName))
            return "mcp__" + s.Function!.ServerName;

        return string.Empty;
    }

    /// <summary>
    /// 将 enabled 模块的动态工具贡献并入已结算工具 id 集（去重、稳定排序）。
    /// </summary>
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
