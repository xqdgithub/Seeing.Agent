using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Agent 注册表接口 - 管理代理的注册、发现和生命周期
    /// <para>
    /// 提供统一的代理发现和管理能力，支持：
    /// - 内置代理（build, explore, plan 等）
    /// - 配置文件扩展代理
    /// - 权限筛选和代理可用性判断
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

        /// <summary>
        /// 获取 Agent 并合并 MD 配置
        /// </summary>
        /// <param name="name">Agent 名称</param>
        /// <param name="provider">可选的 provider 覆盖</param>
        /// <param name="model">可选的 model 覆盖</param>
        /// <returns>合并配置后的 Agent 信息，不存在则返回 null</returns>
        Task<AgentDefinition?> GetAgentWithMergedConfigAsync(
            string name,
            string? provider = null,
            string? model = null);

        /// <summary>获取所有子 Agent（mode != Primary）</summary>
        /// <returns>子 Agent 信息列表</returns>
        Task<IReadOnlyList<AgentDefinition>> GetSubAgentsAsync();

        /// <summary>获取所有主 Agent（mode == Primary 或 mode == All 且 hidden != true）</summary>
        /// <returns>主 Agent 信息列表</returns>
        /// <remarks>
        /// AgentMode.All 模式的代理可同时作为主代理和子代理，
        /// 因此也会包含在此列表中。这允许通用代理出现在 UI 选择列表中。
        /// </remarks>
        Task<IReadOnlyList<AgentDefinition>> GetPrimaryAgentsAsync();

        /// <summary>获取默认 Agent 名称</summary>
        /// <returns>默认 Agent 名称</returns>
        Task<string> GetDefaultAgentNameAsync();

        /// <summary>设置默认 Agent（持久化）</summary>
        /// <param name="name">Agent 名称</param>
        /// <exception cref="ArgumentException">Agent 不存在</exception>
        Task SetDefaultAgentAsync(string name);

        /// <summary>更新 Agent 的模型配置（持久化）</summary>
        /// <param name="agentName">Agent 名称</param>
        /// <param name="model">模型引用（provider/model 格式）</param>
        /// <exception cref="ArgumentException">Agent 不存在</exception>
        Task UpdateAgentModelAsync(string agentName, ModelReference model);

        /// <summary>获取 Agent 的有效模型（优先使用运行时设置，回退到配置）</summary>
        /// <param name="agentName">Agent 名称</param>
        /// <returns>模型引用，未配置则返回 null</returns>
        Task<ModelReference?> GetEffectiveModelAsync(string agentName);

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

        /// <summary>获取或创建 Agent 实例（用于执行）</summary>
        /// <param name="name">Agent 名称</param>
        /// <returns>Agent 实例，不存在则返回 null</returns>
        IAgent? GetOrCreateAgentInstance(string name);
    }
}
