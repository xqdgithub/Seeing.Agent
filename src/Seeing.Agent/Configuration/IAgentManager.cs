using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 管理器接口 - 统一管理 Agent 的注册、发现、配置和运行时
    /// </summary>
    public interface IAgentManager
    {
        #region Agent 发现和查询

        /// <summary>
        /// 获取所有 Agent 信息
        /// </summary>
        Task<IReadOnlyList<Core.Models.AgentDefinition>> GetAgentsAsync();

        /// <summary>
        /// 获取单个 Agent 信息
        /// </summary>
        Task<Core.Models.AgentDefinition?> GetAgentAsync(string name);

        /// <summary>
        /// 获取所有子代理
        /// </summary>
        Task<IReadOnlyList<Core.Models.AgentDefinition>> GetSubAgentsAsync();

        /// <summary>
        /// 获取所有主代理
        /// </summary>
        Task<IReadOnlyList<Core.Models.AgentDefinition>> GetPrimaryAgentsAsync();

        /// <summary>
        /// 检查 Agent 是否存在
        /// </summary>
        bool HasAgent(string name);

        /// <summary>
        /// 获取可访问的子代理（根据权限筛选）
        /// </summary>
        Task<IReadOnlyList<Core.Models.AgentDefinition>> GetAccessibleSubAgentsAsync(IReadOnlyList<PermissionRuleEntry> callerPermissions);

        #endregion

        #region 默认 Agent 管理

        /// <summary>
        /// 获取默认 Agent 名称
        /// </summary>
        Task<string> GetDefaultAgentNameAsync();

        /// <summary>
        /// 设置默认 Agent
        /// </summary>
        Task SetDefaultAgentAsync(string name);

        #endregion

        #region 运行时模型管理

        /// <summary>
        /// 更新 Agent 的运行时模型设置
        /// </summary>
        Task UpdateAgentModelAsync(string agentName, ModelReference model);

        /// <summary>
        /// 获取 Agent 的有效模型
        /// </summary>
        Task<ModelReference?> GetEffectiveModelAsync(string agentName);

        #endregion

        #region Agent 注册

        /// <summary>
        /// 注册 Agent
        /// </summary>
        Task RegisterAgentAsync(Core.Models.AgentDefinition agentInfo);

        /// <summary>
        /// 注销 Agent
        /// </summary>
        bool UnregisterAgent(string name);

        /// <summary>
        /// 获取或创建 Agent 实例
        /// </summary>
        IAgent? GetOrCreateAgentInstance(string name);

        #endregion

        #region 配置编辑（统一模型）

        /// <summary>
        /// 获取所有 Agent 的编辑模型
        /// </summary>
        Task<IReadOnlyList<AgentEditModel>> GetAllEditModelsAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取单个 Agent 的编辑模型
        /// </summary>
        Task<AgentEditModel?> GetEditModelAsync(string agentName, CancellationToken ct = default);

        /// <summary>
        /// 保存 Agent 编辑模型
        /// </summary>
        /// <param name="model">编辑模型</param>
        /// <param name="level">保存层级</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否保存成功</returns>
        Task<bool> SaveEditModelAsync(AgentEditModel model, ConfigLevel level, CancellationToken ct = default);

        /// <summary>
        /// 设置 Agent 的禁用状态（通过 MD 配置的 disabled 字段）
        /// </summary>
        Task<bool> SetAgentDisabledAsync(string name, bool disabled, CancellationToken ct = default);

        #endregion

        #region MD 配置文件管理

        /// <summary>
        /// 获取所有 MD 配置信息
        /// </summary>
        Task<IReadOnlyList<AgentMdInfo>> GetAllMdInfoAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取 MD 配置文件内容
        /// </summary>
        Task<string?> GetMdContentAsync(string name, ConfigLevel level, CancellationToken ct = default);

        /// <summary>
        /// 创建新的 Agent MD 配置
        /// </summary>
        Task<AgentEditModel> CreateMdConfigAsync(string name, ConfigLevel level, CancellationToken ct = default);

        /// <summary>
        /// 保存 MD 配置文件内容
        /// </summary>
        Task<bool> SaveMdContentAsync(string name, ConfigLevel level, string content, CancellationToken ct = default);

        /// <summary>
        /// 删除 MD 配置文件
        /// </summary>
        Task<bool> DeleteMdConfigAsync(string name, ConfigLevel level, CancellationToken ct = default);

        /// <summary>
        /// 获取 MD 配置文件路径
        /// </summary>
        string GetMdFilePath(string name, ConfigLevel level);

        /// <summary>
        /// 获取默认 MD 模板
        /// </summary>
        string GetDefaultMdTemplate(string agentName);

        /// <summary>
        /// 将编辑模型转换为 MD 内容
        /// </summary>
        string GenerateMdContent(AgentEditModel model);

        #endregion

        #region 事件

        /// <summary>
        /// Agent 配置变更事件
        /// </summary>
        event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;

        #endregion
    }
}
