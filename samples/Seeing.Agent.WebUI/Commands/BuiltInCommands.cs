using AntDesign;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Commands
{
    /// <summary>
    /// 内置命令定义 - 提供基础会话操作、Agent 管理、工具管理等命令
    /// </summary>
    public static class BuiltInCommands
    {
        /// <summary>
        /// 内置命令名称集合（用于冲突检测）
        /// </summary>
        public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            "help", "clear", "new", "model", "agent", "mcp", "skill",
            "init", "compact", "undo", "redo", "share", "fork", "terminal"
        };

        /// <summary>
        /// 获取所有内置命令
        /// </summary>
        public static IEnumerable<CommandItemViewModel> GetAll()
        {
            // Basic commands
            yield return Create("help", "显示可用命令列表", IconType.Outline.QuestionCircle, "Basic");
            yield return Create("clear", "清空当前上下文", IconType.Outline.Clear, "Basic");
            
            // Session commands
            yield return Create("new", "新建会话", IconType.Outline.PlusCircle, "Session", "Ctrl+Shift+N");
            yield return Create("compact", "压缩上下文", IconType.Outline.Compress, "Session");
            yield return Create("undo", "撤销上一条消息", IconType.Outline.Undo, "Session", "Ctrl+Z");
            yield return Create("redo", "重做", IconType.Outline.Redo, "Session", "Ctrl+Y");
            yield return Create("share", "分享会话", IconType.Outline.ShareAlt, "Session");
            yield return Create("fork", "分叉会话", IconType.Outline.Fork, "Session");
            
            // Agent commands
            yield return Create("model", "切换模型", IconType.Outline.Setting, "Agent", "Ctrl+'");
            yield return Create("agent", "切换 Agent", IconType.Outline.Robot, "Agent", "Ctrl+.");
            
            // Tools commands
            yield return Create("mcp", "管理 MCP 服务器", IconType.Outline.Api, "Tools", "Ctrl+;");
            yield return Create("skill", "技能列表", IconType.Outline.Book, "Tools");
            
            // System commands
            yield return Create("init", "初始化项目", IconType.Outline.Rocket, "System");
            
            // View commands
            yield return Create("terminal", "切换终端", IconType.Outline.Code, "View", "Ctrl+`");
        }

        /// <summary>
        /// 检查名称是否与内置命令冲突
        /// </summary>
        public static bool IsConflict(string name) => Names.Contains(name);

        private static CommandItemViewModel Create(
            string name,
            string description,
            string iconType,
            string category,
            string? keybind = null)
        {
            return new CommandItemViewModel
            {
                Value = $"/{name}",
                Name = name,
                Description = description,
                IconType = iconType,
                IsBuiltIn = true,
                Category = category,
                Priority = 2,
                Keybind = keybind,
                Source = "built-in"
            };
        }
    }
}
