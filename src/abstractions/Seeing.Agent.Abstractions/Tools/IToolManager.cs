using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具管理器契约 - 工具的注册、查询、启用状态与执行。
/// <para>
/// 读侧分两个问题，不要混用：
/// <see cref="GetRegisteredTools"/> 回答"注册表里有哪些工具"（管理视图，含被禁用项）；
/// <see cref="GetToolSchemasAsync"/> 回答"本会话+Agent 本轮能用哪些工具"（运行视图，经结算与过滤）。
/// </para>
/// </summary>
public interface IToolManager
{
    /// <summary>
    /// 获取**全部已注册**工具（含被禁用的），供管理/配置界面使用。
    /// 这不是"本轮模型可用工具集"——那需要经会话结算与 Agent 过滤，走 <see cref="GetToolSchemasAsync"/>。
    /// </summary>
    IReadOnlyCollection<ITool> GetRegisteredTools();

    /// <summary>按 ID 获取工具（含被禁用项）。</summary>
    ITool? GetTool(string id);

    /// <summary>查询工具的全局启用状态（用户级 + 项目级禁用集的并集判定）。</summary>
    bool IsToolEnabled(string toolId);

    /// <summary>工具启用状态变更通知（供管理界面刷新）。</summary>
    event Action? OnToolStateChanged;

    /// <summary>注册工具（自动应用装饰器链）</summary>
    Task RegisterToolAsync(ITool tool, CancellationToken cancellationToken = default);

    /// <summary>注销工具；未注册时返回 false</summary>
    bool UnregisterTool(string toolId);

    /// <summary>注销工具（异步）；未注册时返回 false</summary>
    Task<bool> UnregisterToolAsync(string toolId, CancellationToken cancellationToken = default);

    /// <summary>启用/禁用工具</summary>
    Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按「层1∩层2已结算 tool id 集」+ Agent Allowed/Denied（层3）生成 schema。
    /// 唯一计算点在 ExecutionRunner；本方法不做会话级结算。
    /// </summary>
    Task<IReadOnlyList<FunctionToolSchema>> GetToolSchemasAsync(
        IReadOnlyCollection<string> settledToolIds,
        AgentDefinition agent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行工具调用（权限检查由 AgentExecutor 统一处理）
    /// <param name="toolCall">工具调用。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="emitAsync">事件出口（可空）。</param>
    /// <param name="permissionAuthorizer">执行级授权器（可空）。</param>
    /// <param name="agent">当前执行所属 Agent（可空）。</param>
    /// <param name="toolScope">执行级工具可见性作用域（可空）。</param>
    /// <param name="skillSnapshot">本轮执行钉住的技能快照（可空；透传到 <see cref="ToolContext.SkillSnapshot"/>）。</param>
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        string sessionId = "",
        CancellationToken cancellationToken = default,
        Func<IMessageEvent, ValueTask>? emitAsync = null,
        IPermissionAuthorizer? permissionAuthorizer = null,
        AgentDefinition? agent = null,
        IExecutionToolScope? toolScope = null,
        SkillCatalogSnapshot? skillSnapshot = null);
}
