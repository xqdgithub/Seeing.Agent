using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Agent 运行时管理接口 - 管理运行时设置和模型配置
    /// <para>
    /// 职责：默认代理设置、模型配置、持久化
    /// 不涉及：Agent 注册、权限筛选、实例创建
    /// </para>
    /// <para>
    /// 模型优先级（从高到低）：
    /// 1. 会话级用户设置（SetSessionModelOverrideAsync）
    /// 2. 持久化的运行时设置（UpdateAgentModelAsync）
    /// 3. Agent 代码/配置定义的模型
    /// 4. 上次使用的模型（切换 Agent 时保留）
    /// 5. 全局默认模型
    /// </para>
    /// </summary>
    public interface IAgentRuntimeManager
    {
        /// <summary>模型变更事件 - 当 Agent 的模型配置发生变更时触发</summary>
        event EventHandler<AgentModelChangedEventArgs>? ModelChanged;

        /// <summary>当前 Agent 名称</summary>
        string CurrentAgentName { get; }

        /// <summary>当前使用的模型</summary>
        string? CurrentModel { get; }

        /// <summary>初始化运行时设置（从持久化加载）</summary>
        Task InitializeAsync();

        /// <summary>设置默认 Agent</summary>
        /// <param name="agentName">Agent 名称</param>
        /// <exception cref="ArgumentException">Agent 不存在或不是有效的主代理</exception>
        Task SetDefaultAgentAsync(string agentName);

        /// <summary>获取默认 Agent 名称</summary>
        /// <returns>默认 Agent 名称，未设置则返回 null</returns>
        Task<string?> GetDefaultAgentNameAsync();

        /// <summary>更新 Agent 的模型配置（持久化）</summary>
        /// <param name="agentName">Agent 名称</param>
        /// <param name="model">模型引用</param>
        /// <exception cref="ArgumentException">Agent 不存在</exception>
        Task UpdateAgentModelAsync(string agentName, ModelReference model);

        /// <summary>获取 Agent 的有效模型</summary>
        /// <param name="agentName">Agent 名称</param>
        /// <returns>模型引用，未配置则返回 null</returns>
        Task<ModelReference?> GetEffectiveModelAsync(string agentName);

        /// <summary>应用运行时模型配置到 Agent</summary>
        /// <param name="agent">Agent 信息实例</param>
        void ApplyRuntimeModel(AgentInfo agent);

        /// <summary>
        /// 设置会话级模型（仅当前会话有效，不持久化）
        /// <para>优先级最高，覆盖其他所有模型配置</para>
        /// </summary>
        /// <param name="agentName">Agent 名称</param>
        /// <param name="modelId">模型 ID</param>
        /// <exception cref="ArgumentException">Agent 名称或模型 ID 为空，或模型不存在</exception>
        Task SetSessionModelOverrideAsync(string agentName, string modelId);

        /// <summary>
        /// 切换 Agent 并返回有效模型
        /// <para>自动处理模型状态的保留和回退</para>
        /// </summary>
        /// <param name="newAgentName">新 Agent 名称</param>
        /// <returns>有效模型引用</returns>
        Task<ModelReference?> SwitchAgentAsync(string newAgentName);

        /// <summary>
        /// 清除会话级模型设置
        /// </summary>
        /// <param name="agentName">Agent 名称，为 null 则清除所有</param>
        void ClearSessionModelOverride(string? agentName = null);
    }

    /// <summary>
    /// Agent 模型变更事件参数
    /// </summary>
    public class AgentModelChangedEventArgs : EventArgs
    {
        /// <summary>Agent 名称</summary>
        public string AgentName { get; set; } = string.Empty;

        /// <summary>旧模型（可能为 null）</summary>
        public ModelReference? OldModel { get; set; }

        /// <summary>新模型（可能为 null，表示清除模型配置）</summary>
        public ModelReference? NewModel { get; set; }

        /// <summary>变更时间</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>变更来源</summary>
        public ModelChangeSource Source { get; set; } = ModelChangeSource.Manual;
    }

    /// <summary>
    /// 模型变更来源
    /// </summary>
    public enum ModelChangeSource
    {
        /// <summary>手动变更（通过 API 调用）</summary>
        Manual,

        /// <summary>配置文件热重载</summary>
        ConfigReload,

        /// <summary>初始化加载</summary>
        Initialization,

        /// <summary>会话级设置（不持久化）</summary>
        Session
    }
}