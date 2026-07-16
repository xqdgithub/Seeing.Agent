using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 配置管理器接口 - 统一管理 Agent 的读取和编辑
    /// </summary>
    public interface IAgentConfigManager
    {
        #region 发现和查询

        /// <summary>
        /// 发现所有 Agent 配置文件
        /// </summary>
        Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取所有 Agent 的编辑模型（合并内置定义 + MD 覆盖）
        /// </summary>
        Task<IReadOnlyList<AgentEditModel>> GetAllAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取单个 Agent 的编辑模型
        /// </summary>
        /// <param name="agentName">Agent 名称</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>编辑模型，如果不存在返回 null</returns>
        Task<AgentEditModel?> GetAsync(string agentName, CancellationToken ct = default);

        /// <summary>
        /// 获取所有 MD 配置信息（用于列表显示）
        /// </summary>
        Task<IReadOnlyList<AgentMdInfo>> GetAllMdInfoAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取 MD 配置文件的原始内容
        /// </summary>
        Task<string?> GetMdContentAsync(string name, ConfigLevel level, CancellationToken ct = default);

        #endregion

        #region 创建和保存

        /// <summary>
        /// 创建新的 Agent MD 配置
        /// </summary>
        /// <param name="name">Agent 名称</param>
        /// <param name="level">配置层级</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>创建的编辑模型</returns>
        Task<AgentEditModel> CreateAsync(string name, ConfigLevel level, CancellationToken ct = default);

        /// <summary>
        /// 保存 Agent 编辑模型到 MD 文件
        /// </summary>
        /// <param name="model">编辑模型</param>
        /// <param name="level">保存层级</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否保存成功</returns>
        Task<bool> SaveAsync(AgentEditModel model, ConfigLevel level, CancellationToken ct = default);

        /// <summary>
        /// 保存 MD 配置文件的原始内容
        /// </summary>
        Task<bool> SaveMdContentAsync(string name, ConfigLevel level, string content, CancellationToken ct = default);

        #endregion

        #region 删除

        /// <summary>
        /// 删除 Agent 的 MD 配置文件
        /// </summary>
        /// <param name="name">Agent 名称</param>
        /// <param name="level">配置层级</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否删除成功</returns>
        Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default);

        #endregion

        #region 工具方法

        /// <summary>
        /// 获取 MD 配置文件路径
        /// </summary>
        string GetFilePath(string name, ConfigLevel level);

        /// <summary>
        /// 获取默认 MD 模板
        /// </summary>
        string GetDefaultTemplate(string agentName);

        #endregion

        #region 事件

        /// <summary>
        /// 配置变更事件
        /// </summary>
        event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;

        #endregion
    }
}
