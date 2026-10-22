using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 会话级工具视图端口：按会话结算（scenario / 模块启用 / 工具禁用）+ Agent 过滤 + 暴露裁剪，
/// 产出本轮**一个**工具视图，作为"工具列表"的唯一来源。
/// <para>
/// 主执行链（Hosting ExecutionRunner）、Scheduler 旁路、以及需要了解工具清单的元工具
/// 必须经同一出口取用，不得各自去问工具注册表。
/// </para>
/// <para>
/// 契约：本端口**只读**，不写会话状态；未注册结算依赖（<c>IModuleCatalog</c> 等）时返回空视图，不抛异常。
/// 会话状态的提交由执行入口在每轮开头单独完成。
/// </para>
/// </summary>
public interface ISessionToolSchemaResolver
{
    /// <summary>
    /// 解析指定会话 + Agent 的本轮工具视图（按需加载已存在的会话；不存在时返回空视图，不创建会话）。
    /// </summary>
    Task<SessionToolSchemaResolution> ResolveAsync(
        string sessionId,
        AgentDefinition agent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析已加载会话 + Agent 的本轮工具视图（执行入口用此重载，复用同一会话实例）。
    /// </summary>
    Task<SessionToolSchemaResolution> ResolveAsync(
        SessionData session,
        AgentDefinition agent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话级工具视图。
/// <para>
/// <see cref="Tools"/> 是本轮全部可用工具（含未内联的），其余成员都是它的派生投影，
/// 调用方不应再从别处查询工具清单。
/// </para>
/// </summary>
/// <param name="Discovered">
/// 本轮结算后的发现集（已裁剪到会话仍可用的工具）。执行入口用它提交会话状态，
/// 裁剪语义只存在于结算引擎一处，此处只做传递。
/// </param>
public sealed record SessionToolSchemaResolution(
    IReadOnlyList<ExposedTool> Tools,
    IReadOnlyList<string> SectionIds,
    ToolExposureLayout Layout,
    string? CatalogText,
    int SchemaTokens,
    int CatalogTokens,
    IReadOnlyList<string>? Discovered = null,
    bool IsEscalated = false)
{
    /// <summary>空视图。</summary>
    public static SessionToolSchemaResolution Empty { get; } = new(
        Array.Empty<ExposedTool>(), Array.Empty<string>(), ToolExposureLayout.Flat, null, 0, 0);

    /// <summary>本轮内联进请求的工具（按物化顺序）。</summary>
    public IReadOnlyList<FunctionToolSchema> InlineSchemas =>
        Tools.Where(t => t.Mode == ToolExposureMode.Inline).Select(t => t.Schema).ToList();

    /// <summary>本轮可被检索发现但未内联的工具（检索语料）。</summary>
    public IReadOnlyList<ExposedTool> DeferredTools =>
        Tools.Where(t => t.Mode == ToolExposureMode.Deferred).ToList();

    /// <summary>内联工具数。</summary>
    public int InlineToolCount => Tools.Count(t => t.Mode == ToolExposureMode.Inline);

    /// <summary>延迟工具数。</summary>
    public int DeferredToolCount => Tools.Count(t => t.Mode == ToolExposureMode.Deferred);

    /// <summary>本轮 toolbar 中是否存在指定 id 的工具（任意模式）。</summary>
    public bool Contains(string toolId) =>
        Tools.Any(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 该工具本轮是否**可直接调用**：schema 已随本视图下发（<see cref="ToolExposureMode.Inline"/>）。
    /// <para>执行层据此做可见集校验；未下发 schema 的调用一律拒绝。</para>
    /// </summary>
    public bool IsCallable(string toolId) =>
        Tools.Any(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase)
                       && t.Mode == ToolExposureMode.Inline);

    /// <summary>
    /// 该工具本轮是否以**目录条目延迟暴露**（<see cref="ToolExposureMode.Deferred"/>）：
    /// 模型只看到 id 与摘要，需先经检索才能取得 schema。
    /// </summary>
    public bool IsDeferred(string toolId) =>
        Tools.Any(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase)
                       && t.Mode == ToolExposureMode.Deferred);

    /// <summary>本轮是否有检索工具可用（决定目录引导文案与执行层提示）。</summary>
    public bool HasSearchTool =>
        Contains(ToolIdConventions.SearchToolId) &&
        Tools.Any(t => string.Equals(t.Id, ToolIdConventions.SearchToolId, StringComparison.OrdinalIgnoreCase)
                       && t.Mode == ToolExposureMode.Inline);
}
