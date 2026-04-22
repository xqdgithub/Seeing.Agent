using Spectre.Console;
using Spectre.Console.Rendering;
using System.Collections.Generic;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Tui.Core;

namespace Seeing.Agent.Tui.UI;

/// <summary>
/// 错误面板工厂 - 使用红色边框展示错误信息
/// </summary>
public static class ErrorPanel
{
    /// <summary>
    /// 根据错误事件创建错误面板
    /// </summary>
    /// <param name="evt">错误事件</param>
    /// <param name="isExpanded">是否展开显示错误详情</param>
    /// <returns>渲染用的 Panel</returns>
    public static Panel Render(ErrorEvent evt, bool isExpanded = false)
    {
        var source = evt.Source ?? "未知来源";
        var message = evt.Message ?? string.Empty;

        // 构建标题：来源信息
        var headerText = $"{ColorScheme.Icons.Error} 错误 - 来源: {EscapeMarkup(source)}";

        // 内容：主错误信息
        var rows = new List<IRenderable>
        {
            new Markup($"[{ColorScheme.ErrorColor}]{EscapeMarkup(message)}[/]")
        };

        // 4. 详细信息（可选）
        if (isExpanded && evt.Exception != null)
        {
            var details = evt.Exception!.ToString();
            rows.Add(new Markup($"[dim]详情: {EscapeMarkup(details)}[/]"));
        }

        // 5. 组装 Panel
        var panel = new Panel(new Rows(rows))
        {
            Header = new PanelHeader(headerText),
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Red),
            Padding = new Padding(1, 0, 0, 0),
            Expand = false
        };

        return panel;
    }

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    /// <param name="text">原始文本</param>
    /// <returns>转义后的文本</returns>
    private static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 使用与渲染服务相同的转义规则，确保 [ 和 ] 不会破坏 Markup
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}
