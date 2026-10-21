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
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 会话级 schema 结算内部结果：schema + 分节 id。
/// </summary>
internal sealed record SessionSchemaSettlement(
    IReadOnlyList<FunctionToolSchema> ToolSchemas,
    IReadOnlyList<string> SectionIds);

/// <summary>
/// 会话级工具 schema 解析（<see cref="ISessionToolSchemaResolver"/> 唯一实现）：
/// 会话级结算（scenario / 模块启用 / 工具禁用）+ 动态工具贡献并入 + Agent 过滤 + 分节 id 收集。
/// <para>
/// schema 单点纪律：主执行链（ExecutionRunner）与旁路执行（Scheduler AgentJob / HeartbeatJob
/// 直连 <see cref="Seeing.Agent.Abstractions.Agents.IAgentExecutor"/>）共用本结算，
/// 保证任何执行路径的 LLM 请求都携带真实工具 schema。
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
        return new SessionToolSchemaResolution(settlement.ToolSchemas, settlement.SectionIds);
    }

    /// <inheritdoc />
    public async Task<SessionToolSchemaResolution> ResolveAsync(
        SessionData session,
        AgentDefinition agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);

        // 复用调用方已加载的 session 实例；结算依赖（IToolManager 等）均为单例，
        // 从根容器自开作用域与主执行链 turn 作用域等价，且避免二次加载读到未落盘前的旧状态。
        using var scope = _rootProvider.CreateScope();
        var settlement = await SettleAsync(session, agent, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        return new SessionToolSchemaResolution(settlement.ToolSchemas, settlement.SectionIds);
    }

    /// <summary>
    /// 会话级结算 + schema 唯一计算（本类私有核心，仅供两个公开端口重载调用）。
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

        return new SessionSchemaSettlement(toolSchemas, sectionIds);
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
