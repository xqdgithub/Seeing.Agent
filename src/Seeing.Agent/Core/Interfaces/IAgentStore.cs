using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Agent 存储接口 - 纯存储操作，不包含业务逻辑
    /// <para>
    /// 职责：管理 Agent 实例的注册、查询和注销
    /// 不涉及：运行时设置、权限筛选、默认代理等业务逻辑
    /// </para>
    /// </summary>
    public interface IAgentStore
    {
        /// <summary>注册 Agent</summary>
        /// <param name="agentInfo">Agent 信息</param>
        Task RegisterAsync(AgentInfo agentInfo);

        /// <summary>注销 Agent</summary>
        /// <param name="name">Agent 名称</param>
        /// <returns>是否成功注销</returns>
        bool Unregister(string name);

        /// <summary>获取指定 Agent</summary>
        /// <param name="name">Agent 名称</param>
        /// <returns>Agent 信息，不存在则返回 null</returns>
        Task<AgentInfo?> GetAsync(string name);

        /// <summary>获取所有 Agent</summary>
        /// <returns>Agent 信息列表</returns>
        Task<IReadOnlyList<AgentInfo>> GetAllAsync();

        /// <summary>检查 Agent 是否存在</summary>
        /// <param name="name">Agent 名称</param>
        /// <returns>是否存在</returns>
        bool Has(string name);
    }
}