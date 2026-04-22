using System;
using System.Reflection;
using Spectre.Console;
using Seeing.Agent.SpectreTui.Core.State;
using Seeing.Agent.SpectreTui.Core;

namespace Seeing.Agent.SpectreTui.UI
{
    /// <summary>
    /// 简易状态栏，使用 Spectre.Console Markup 显示关键信息
    /// 依赖：AgentContext (Seeing.Agent.Tui.Core.State) 与 静态颜色/布局配置 (ColorScheme, LayoutConfig)
    /// </summary>
    public class StatusBar
    {
        private readonly AgentContext _context;

        public StatusBar(AgentContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        private int GetMessageCount()
        {
            // 尝试从上下文读取消息数量，如果不存在则返回 0
            try
            {
                var prop = _context.GetType().GetProperty("MessageCount", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var value = prop.GetValue(_context);
                    if (value is int i) return i;
                    if (value is long l) return (int)l;
                }
            }
            catch
            {
                // 忽略反射异常，回退到默认
            }
            return 0;
        }

        public Markup RenderMarkup()
        {
            string agentName = _context.CurrentAgentKey;
            string model = _context.CurrentModel ?? "N/A";

            int toolCount = _context.ToolCount;
            int skillCount = _context.SkillCount;
            int mcpCount = _context.McpServerCount;
            int messageCount = GetMessageCount();

            // 构建一个紧凑的单行状态栏，使用不同颜色区分信息
            string text =
                $"[{ColorScheme.PrimaryColor}]Agent: {agentName}[/]  " +
                $"[{ColorScheme.SecondaryColor}]Model: {model}[/]  " +
                $"[{ColorScheme.SuccessColor}]Tools: {toolCount}[/]  " +
                $"[{ColorScheme.ToolPendingColor}]Skills: {skillCount}[/]  " +
                $"[{ColorScheme.WarningColor}]MCPs: {mcpCount}[/]  " +
                $"[{ColorScheme.InfoColor}]Msgs: {messageCount}[/]";

            return new Markup(text);
        }
    }
}
