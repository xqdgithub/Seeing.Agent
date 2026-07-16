namespace Seeing.Agent.WebUI.Models
{
    /// <summary>
    /// 命令项视图模型（用于 Mentions 自动完成）
    /// </summary>
    public class CommandItemViewModel
    {
        /// <summary>命令值 (格式: /name)</summary>
        public string Value { get; set; } = "";

        /// <summary>命令名称</summary>
        public string Name { get; set; } = "";

        /// <summary>命令描述</summary>
        public string Description { get; set; } = "";

        /// <summary>图标类型</summary>
        public string IconType { get; set; } = "";

        /// <summary>是否为工具</summary>
        public bool IsTool { get; set; }

        /// <summary>是否为技能</summary>
        public bool IsSkill { get; set; }

        /// <summary>是否为内置命令</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>是否为自定义命令</summary>
        public bool IsCustom { get; set; }

        /// <summary>命令分类 (Basic, Session, Agent, Tools, System, View, Custom)</summary>
        public string Category { get; set; } = "";

        /// <summary>命令别名</summary>
        public string[] Aliases { get; set; } = Array.Empty<string>();

        /// <summary>排序优先级（数值越小优先级越高）</summary>
        public int Priority { get; set; } = 100;

        /// <summary>快捷键提示（仅显示，不实现监听）</summary>
        public string? Keybind { get; set; }

        /// <summary>是否禁用</summary>
        public bool IsDisabled { get; set; }

        /// <summary>禁用原因</summary>
        public string? DisabledReason { get; set; }

        /// <summary>命令来源 (built-in, user, project)</summary>
        public string? Source { get; set; }
    }
}