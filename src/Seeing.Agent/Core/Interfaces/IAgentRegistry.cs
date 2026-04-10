using Seeing.Agent.Core.Models;
using System.Collections.Generic;

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
        Task<IReadOnlyList<AgentInfo>> GetAgentsAsync();

        /// <summary>获取指定名称的 Agent</summary>
        /// <param name="name">Agent 名称</param>
        /// <returns>Agent 信息，不存在则返回 null</returns>
        Task<AgentInfo?> GetAgentAsync(string name);

        /// <summary>获取所有子 Agent（mode != Primary）</summary>
        /// <returns>子 Agent 信息列表</returns>
        Task<IReadOnlyList<AgentInfo>> GetSubAgentsAsync();

        /// <summary>获取所有主 Agent（mode == Primary 或 mode == All 且 hidden != true）</summary>
        /// <returns>主 Agent 信息列表</returns>
        /// <remarks>
        /// AgentMode.All 模式的代理可同时作为主代理和子代理，
        /// 因此也会包含在此列表中。这允许通用代理出现在 UI 选择列表中。
        /// </remarks>
        Task<IReadOnlyList<AgentInfo>> GetPrimaryAgentsAsync();

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
        Task RegisterAgentAsync(AgentInfo agentInfo);

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
        Task<IReadOnlyList<AgentInfo>> GetAccessibleSubAgentsAsync(IReadOnlyList<PermissionRule> callerPermissions);

        /// <summary>获取或创建 Agent 实例（用于执行）</summary>
        /// <param name="name">Agent 名称</param>
        /// <returns>Agent 实例，不存在则返回 null</returns>
        IAgent? GetOrCreateAgentInstance(string name);
    }

    /// <summary>
    /// Agent 信息 - 代理的完整元数据定义
    /// </summary>
    public class AgentInfo
    {
        /// <summary>Agent 名称（唯一标识）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Agent 描述</summary>
        public string? Description { get; set; }

        /// <summary>Agent 模式：Primary（主代理）、SubAgent（子代理）、All（通用）</summary>
        public AgentMode Mode { get; set; } = AgentMode.All;

        /// <summary>是否为内置 Agent</summary>
        public bool IsNative { get; set; }

        /// <summary>是否隐藏（不在 UI 中显示）</summary>
        public bool IsHidden { get; set; }

        /// <summary>温度参数（LLM 调用）</summary>
        public double? Temperature { get; set; }

        /// <summary>TopP 参数（LLM 调用）</summary>
        public double? TopP { get; set; }

        /// <summary>颜色标识（UI 显示）</summary>
        public string? Color { get; set; }

        /// <summary>权限规则集</summary>
        public List<PermissionRule> Permissions { get; set; } = new();

        /// <summary>允许的工具列表（Tool Ids）</summary>
        public List<string> AllowedTools { get; set; } = new();

        /// <summary>禁止的工具列表（Tool Ids）</summary>
        public List<string> DeniedTools { get; set; } = new();

        /// <summary>模型配置（可选，默认使用系统配置）</summary>
        public ModelReference? Model { get; set; }

        /// <summary>变体标识</summary>
        public string? Variant { get; set; }

        /// <summary>系统提示词（可选，覆盖默认）</summary>
        public string? SystemPrompt { get; set; }

        /// <summary>扩展选项</summary>
        public Dictionary<string, object> Options { get; set; } = new();

        /// <summary>最大迭代步骤</summary>
        public int? MaxSteps { get; set; }

        /// <summary>Agent 类型标识（用于分类）</summary>
        public string? Category { get; set; }

        /// <summary>Agent 标签</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// 创建 IAgent 实例（延迟创建，用于执行）
        /// </summary>
        public Func<IAgent>? AgentFactory { get; set; }
    }
}
