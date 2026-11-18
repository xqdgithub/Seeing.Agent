namespace Seeing.Agent.WebUI.Models
{
    /// <summary>
    /// 命令分组（命令面板按来源分组展示，顺序即枚举值）。
    /// </summary>
    public enum CommandGroupKind
    {
        /// <summary>内置命令 / 模块自带命令。</summary>
        BuiltIn = 0,

        /// <summary>技能（后端为每个启用技能注册的 /&lt;技能名&gt;）。</summary>
        Skill = 1,

        /// <summary>用户 / 项目自定义的 Markdown 命令。</summary>
        Custom = 2,

        /// <summary>扩展命令（MCP prompt、Category=Extension 等）。</summary>
        Extension = 3
    }

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

        /// <summary>面板分组（按来源；渲染时同组连续输出）。</summary>
        public CommandGroupKind Group { get; set; }

        /// <summary>面板分组标题（组标题在列表内渲染一次）。</summary>
        public string GroupTitle { get; set; } = "";

        /// <summary>分组排序值（与 <see cref="Group"/> 同源，供排序使用）。</summary>
        public int GroupOrder => (int)Group;
    }
}