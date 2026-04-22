namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// 配置持久化接口 - 支持运行时设置的保存和加载
    /// </summary>
    public interface IConfigurationPersistence
    {
        /// <summary>加载运行时设置</summary>
        /// <returns>运行时设置，不存在则返回默认值</returns>
        Task<RuntimeSettings> LoadAsync(CancellationToken cancellationToken = default);

        /// <summary>保存运行时设置</summary>
        /// <param name="settings">要保存的设置</param>
        Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default);

        /// <summary>重置为默认设置</summary>
        Task ResetAsync(CancellationToken cancellationToken = default);

        /// <summary>设置文件路径</summary>
        string SettingsFilePath { get; }
    }

    /// <summary>
    /// 运行时设置 - 持久化的用户偏好
    /// </summary>
    public class RuntimeSettings
    {
        /// <summary>默认 Agent 名称</summary>
        public string? DefaultAgent { get; set; }

        /// <summary>默认模型 ID（格式: provider/model）</summary>
        public string? DefaultModel { get; set; }

        /// <summary>Agent 特定的模型配置（Agent 名称 → 模型 ID）</summary>
        public Dictionary<string, string> AgentModels { get; set; } = new();

        /// <summary>最后更新时间</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        /// <summary>设置版本（用于迁移）</summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// 获取指定 Agent 的模型 ID
        /// </summary>
        public string? GetAgentModel(string agentName)
        {
            return AgentModels.TryGetValue(agentName, out var model) ? model : null;
        }

        /// <summary>
        /// 设置指定 Agent 的模型 ID
        /// </summary>
        public void SetAgentModel(string agentName, string modelId)
        {
            AgentModels[agentName] = modelId;
            UpdatedAt = DateTime.Now;
        }

        /// <summary>
        /// 清除指定 Agent 的模型配置（回退到默认）
        /// </summary>
        public void ClearAgentModel(string agentName)
        {
            AgentModels.Remove(agentName);
            UpdatedAt = DateTime.Now;
        }
    }
}