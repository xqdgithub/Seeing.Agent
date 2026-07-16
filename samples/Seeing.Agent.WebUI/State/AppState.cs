using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.WebUI.State
{
    /// <summary>
    /// 应用全局状态，管理用户、主题、Agent/Model 列表
    /// <para>
    /// 会话列表由 IChatOrchestrator 统一管理
    /// </para>
    /// <para>
    /// 当前会话的 Agent/Model 由 SessionState 管理
    /// </para>
    /// </summary>
    public class AppState
    {
        public AppState() { }

        /// <summary>
        /// 状态变更事件（通知订阅者重新渲染）
        /// </summary>
        public event Action? OnChange;

        /// <summary>
        /// 触发状态变更通知
        /// </summary>
        public void NotifyChange()
        {
            OnChange?.Invoke();
        }

        /// <summary>当前用户</summary>
        public string CurrentUser { get; set; } = "Guest";

        /// <summary>当前主题</summary>
        public string Theme { get; set; } = "light";

        /// <summary>可用的 Agent 列表（从注册中心获取）</summary>
        public List<AgentDefinition> AvailableAgents { get; set; } = new();

        /// <summary>可用的 Model 列表（从 LlmService 获取）</summary>
        public IReadOnlyDictionary<string, ModelConfig> AvailableModels { get; set; } = new Dictionary<string, ModelConfig>();

        /// <summary>当前会话 ID（由 IChatOrchestrator 管理）</summary>
        public string CurrentSessionId { get; set; } = "";

        /// <summary>侧边栏是否折叠</summary>
        public bool SidebarCollapsed { get; set; } = false;

        /// <summary>是否正在加载</summary>
        public bool IsLoading { get; set; } = false;

        /// <summary>全局错误消息</summary>
        public string? ErrorMessage { get; set; }
    }
}