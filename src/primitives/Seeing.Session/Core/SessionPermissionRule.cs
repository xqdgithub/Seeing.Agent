namespace Seeing.Session.Core
{
    /// <summary>
    /// 可序列化的会话权限规则（子 Agent 权限快照用，避免 Session 包依赖 Agent）。
    /// </summary>
    public class SessionPermissionRule
    {
        /// <summary>权限种类（如 Tool、File、Shell）</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>匹配模式</summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>效果：Allow / Deny / Ask</summary>
        public string Effect { get; set; } = string.Empty;

        /// <summary>优先级（数值越大越优先，与 Agent 侧约定一致）</summary>
        public int Priority { get; set; }
    }
}
