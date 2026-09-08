using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Abstractions.Agents;

/// <summary>
/// Agent 注册表接口 - 管理代理的注册、发现和权限筛选
/// <para>
/// 提供统一的代理发现和管理能力，支持：
/// - 内置代理（build, explore, plan 等）
/// - 配置文件扩展代理
/// - 权限筛选和代理可用性判断
/// </para>
/// <para>
/// 职责：Agent 注册、查询、注销、权限筛选
/// 不涉及：默认 Agent、模型绑定（见 <see cref="IAgentRuntimeManager"/>）
/// </para>
/// </summary>
public interface IAgentRegistry
{
    /// <summary>获取所有已注册的 Agent</summary>
    /// <returns>Agent 信息列表</returns>
    Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync();

    /// <summary>获取指定名称的 Agent</summary>
    /// <param name="name">Agent 名称</param>
    /// <returns>Agent 信息，不存在则返回 null</returns>
    Task<AgentDefinition?> GetAgentAsync(string name);

    /// <summary>获取所有子 Agent（mode != Primary）</summary>
    /// <returns>子 Agent 信息列表</returns>
    Task<IReadOnlyList<AgentDefinition>> GetSubAgentsAsync();

    /// <summary>
    /// 获取 TaskTool 可委托的 Agent：Native 运行时、非 Primary、未禁用（含 Mode=SubAgent / All）。
    /// </summary>
    Task<IReadOnlyList<AgentDefinition>> GetTaskableAgentsAsync();

    /// <summary>获取所有主 Agent（mode == Primary 或 mode == All 且 hidden != true）</summary>
    /// <returns>主 Agent 信息列表</returns>
    /// <remarks>
    /// AgentMode.All 模式的代理可同时作为主代理和子代理，
    /// 因此也会包含在此列表中。这允许通用代理出现在 UI 选择列表中。
    /// </remarks>
    Task<IReadOnlyList<AgentDefinition>> GetPrimaryAgentsAsync();

    /// <summary>注册新的 Agent</summary>
    /// <param name="agentInfo">Agent 信息</param>
    Task RegisterAgentAsync(AgentDefinition agentInfo);

    /// <summary>注销 Agent</summary>
    /// <param name="name">Agent 名称</param>
    /// <returns>是否成功注销</returns>
    bool UnregisterAgent(string name);

    /// <summary>检查 Agent 是否存在</summary>
    /// <param name="name">Agent 名称</param>
    /// <returns>是否存在</returns>
    bool HasAgent(string name);

    /// <summary>根据权限筛选可访问的子 Agent</summary>
    /// <param name="callerPermissions">调用者的权限规则</param>
    /// <returns>可访问的子 Agent 列表</returns>
    Task<IReadOnlyList<AgentDefinition>> GetAccessibleSubAgentsAsync(IReadOnlyList<PermissionRuleEntry> callerPermissions);
}