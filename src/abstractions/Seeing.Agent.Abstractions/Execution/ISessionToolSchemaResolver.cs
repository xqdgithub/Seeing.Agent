using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 会话级工具 schema 解析端口：按会话结算（scenario / 模块启用 / 工具禁用）+ Agent 过滤，
/// 计算本轮可用 <see cref="FunctionToolSchema"/> 集。
/// <para>
/// schema 单点纪律的共享出口：主执行链（Hosting ExecutionRunner）与旁路执行
/// （Scheduler 的 AgentJob / HeartbeatJob 直连 <c>IAgentExecutor</c>）必须经同一解析，
/// 保证任何执行路径的 LLM 请求都携带真实工具 schema（否则模型只能靠文本「表演」工具调用）。
/// </para>
/// <para>
/// 契约：未注册结算依赖（<c>IModuleCatalog</c> 等）时返回空集，不抛异常。
/// </para>
/// </summary>
public interface ISessionToolSchemaResolver
{
    /// <summary>
    /// 解析指定会话 + Agent 的本轮工具 schema（含会话级结算）。
    /// </summary>
    /// <param name="sessionId">会话 ID（按需加载会话，取其 Scenario / ScenarioOverride）。</param>
    /// <param name="agent">目标 Agent 定义。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SessionToolSchemaResolution> ResolveAsync(
        string sessionId,
        AgentDefinition agent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析已加载会话 + Agent 的本轮工具 schema。主执行链（ExecutionRunner）用此重载：
    /// 复用调用方已加载且可能含未落盘变更的 <see cref="SessionData"/> 实例，避免二次加载读到旧状态。
    /// </summary>
    /// <param name="session">已加载的会话实例（直接使用，不再按 id 重新加载）。</param>
    /// <param name="agent">目标 Agent 定义。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SessionToolSchemaResolution> ResolveAsync(
        SessionData session,
        AgentDefinition agent,
        CancellationToken cancellationToken = default);
}

/// <summary>会话级 schema 解析结果。</summary>
public sealed record SessionToolSchemaResolution(
    IReadOnlyList<FunctionToolSchema> ToolSchemas,
    IReadOnlyList<string> SectionIds,
    IReadOnlyList<ToolCatalogEntry> CatalogEntries = default!,
    int InlineToolCount = 0,
    int DeferredToolCount = 0,
    int EstimatedDefinitionTokens = 0,
    int CatalogTokens = 0,
    ToolExposurePolicyMode ExposureMode = ToolExposurePolicyMode.Auto)
{
    public static SessionToolSchemaResolution Empty { get; } =
        new(Array.Empty<FunctionToolSchema>(), Array.Empty<string>(), Array.Empty<ToolCatalogEntry>());
}
