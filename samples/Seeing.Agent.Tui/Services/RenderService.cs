using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Models;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// Markup 渲染服务 - 使用 Spectre.Console 格式化输出
/// </summary>
public sealed class RenderService
{
    /// <summary>
    /// 渲染用户消息 - 左侧高亮显示
    /// </summary>
    /// <param name="content">消息内容</param>
    /// <returns>渲染后的 Panel</returns>
    public Panel RenderUserMessage(string content)
    {
        var escapedContent = EscapeMarkup(content);
        var markupContent = $"[{ColorScheme.UserColor}]{escapedContent}[/]";
        
        var panel = new Panel(markupContent)
        {
            Header = new PanelHeader($"{ColorScheme.Icons.User} 用户"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan),
            Padding = new Padding(1, 0, 1, 0),
            Expand = false
        };
        
        return panel;
    }
    
    /// <summary>
    /// 渲染助手消息 - 缩进 + Markdown 支持
    /// </summary>
    /// <param name="content">消息内容</param>
    /// <param name="includeHeader">是否包含头部</param>
    /// <returns>渲染后的 Panel</returns>
    public Panel RenderAssistantMessage(string content, bool includeHeader = true)
    {
        // 处理 Markdown 格式（简化版本）
        var formattedContent = FormatMarkdown(content);
        
        var panel = new Panel(formattedContent)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Padding = new Padding(2, 0, 1, 0), // 左侧缩进
            Expand = true
        };
        
        if (includeHeader)
        {
            panel.Header = new PanelHeader($"{ColorScheme.Icons.Assistant} 助手");
        }
        
        return panel;
    }
    
    /// <summary>
    /// 渲染工具调用 - 状态图标 + 可折叠内容
    /// </summary>
    /// <param name="name">工具名称</param>
    /// <param name="status">执行状态</param>
    /// <param name="output">执行输出</param>
    /// <param name="isExpanded">是否展开</param>
    /// <returns>渲染后的 Panel</returns>
    public Panel RenderToolCall(string name, ToolCallStatus status, string? output, bool isExpanded = false)
    {
        var (icon, color) = GetStatusStyle(status);
        var escapedName = EscapeMarkup(name);
        
        // 标题行：状态图标 + 工具名称
        var headerText = $"{icon} [{color}]{escapedName}[/]";
        
        // 内容区域
        var content = output != null && isExpanded
            ? EscapeMarkup(output)
            : $"[{ColorScheme.FoldHintColor}]点击展开详情...[/]";
        
        var panel = new Panel(content)
        {
            Header = new PanelHeader(headerText),
            Border = BoxBorder.Square,
            BorderStyle = new Style(Color.Blue),
            Padding = new Padding(1, 0),
            Expand = false
        };
        
        return panel;
    }
    
    /// <summary>
    /// 渲染工具调用（使用 ToolCallDisplay 模型）
    /// </summary>
    /// <param name="toolCall">工具调用显示模型</param>
    /// <param name="isExpanded">是否展开</param>
    /// <returns>渲染后的 Panel</returns>
    public Panel RenderToolCall(ToolCallDisplay toolCall, bool isExpanded = false)
    {
        return RenderToolCall(
            toolCall.Name,
            toolCall.Status,
            toolCall.Result ?? toolCall.Error,
            isExpanded);
    }
    
    /// <summary>
    /// 渲染思考过程 - 灰色斜体 + 默认折叠
    /// </summary>
    /// <param name="content">思考内容</param>
    /// <param name="isExpanded">是否展开</param>
    /// <returns>渲染后的 Panel</returns>
    public Panel RenderReasoning(string content, bool isExpanded = false)
    {
        var escapedContent = EscapeMarkup(content);
        var icon = isExpanded ? ColorScheme.Icons.Expanded : ColorScheme.Icons.Folded;
        
        // 折叠时只显示摘要
        var displayContent = isExpanded
            ? $"[{ColorScheme.ReasoningColor} italic]{escapedContent}[/]"
            : $"[{ColorScheme.FoldHintColor}]思考过程（点击展开）[/]";
        
        var headerText = $"{icon} [{ColorScheme.ReasoningColor}]💭 思考过程[/]";
        
        var panel = new Panel(displayContent)
        {
            Header = new PanelHeader(headerText),
            Border = BoxBorder.None,
            Padding = new Padding(1, 0),
            Expand = false
        };
        
        return panel;
    }
    
    /// <summary>
    /// 渲染错误消息 - 红色边框
    /// </summary>
    /// <param name="message">错误消息</param>
    /// <returns>渲染后的 Panel</returns>
    public Panel RenderError(string message)
    {
        var escapedMessage = EscapeMarkup(message);
        var markupContent = $"[{ColorScheme.ErrorColor}]{escapedMessage}[/]";
        
        var panel = new Panel(markupContent)
        {
            Header = new PanelHeader($"{ColorScheme.Icons.Error} 错误"),
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Red),
            Padding = new Padding(1, 0),
            Expand = false
        };
        
        return panel;
    }
    
    /// <summary>
    /// 渲染系统消息
    /// </summary>
    /// <param name="content">消息内容</param>
    /// <returns>渲染后的 Panel</returns>
    public Panel RenderSystemMessage(string content)
    {
        var escapedContent = EscapeMarkup(content);
        var markupContent = $"[{ColorScheme.SystemColor}]{escapedContent}[/]";
        
        var panel = new Panel(markupContent)
        {
            Border = BoxBorder.None,
            Padding = new Padding(0),
            Expand = false
        };
        
        return panel;
    }
    
    /// <summary>
    /// 渲染消息显示模型（完整渲染）
    /// </summary>
    /// <param name="message">消息显示模型</param>
    /// <param name="expandTools">是否展开工具调用</param>
    /// <param name="expandReasoning">是否展开思考过程</param>
    /// <returns>渲染结果集合</returns>
    public IEnumerable<IRenderable> RenderMessage(
        MessageDisplay message,
        bool expandTools = false,
        bool expandReasoning = false)
    {
        var results = new List<IRenderable>();
        
        // 1. 思考过程（如果有）
        if (!string.IsNullOrEmpty(message.Reasoning))
        {
            results.Add(RenderReasoning(message.Reasoning, expandReasoning));
        }
        
        // 2. 主内容（根据角色）
        switch (message.Role)
        {
            case "user":
                results.Add(RenderUserMessage(message.Content));
                break;
            case "assistant":
                results.Add(RenderAssistantMessage(message.Content));
                break;
            case "tool":
                // 工具结果消息
                if (!string.IsNullOrEmpty(message.ToolResult))
                {
                    var toolMessage = message.ToolSuccess
                        ? $"[{ColorScheme.SuccessColor}]工具结果: [/]{EscapeMarkup(message.ToolResult)}"
                        : $"[{ColorScheme.ErrorColor}]工具失败: [/]{EscapeMarkup(message.ToolResult)}";
                    results.Add(new Panel(toolMessage)
                    {
                        Border = BoxBorder.Square,
                        BorderStyle = new Style(message.ToolSuccess ? Color.Green : Color.Red),
                        Expand = false
                    });
                }
                break;
            case "system":
                if (message.Content.StartsWith("错误:") || message.Content.StartsWith("Error:"))
                {
                    results.Add(RenderError(message.Content));
                }
                else
                {
                    results.Add(RenderSystemMessage(message.Content));
                }
                break;
        }
        
        // 3. 工具调用列表（如果有）
        foreach (var toolCall in message.ToolCalls)
        {
            results.Add(RenderToolCall(toolCall, expandTools));
        }
        
        return results;
    }
    
    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    /// <param name="text">原始文本</param>
    /// <returns>转义后的安全文本</returns>
    public static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        
        // Spectre.Console Markup 需要转义 [ 和 ]
        return text
            .Replace("[", "[[")
            .Replace("]", "]]");
    }
    
    /// <summary>
    /// 反转义 Markup（还原原始文本）
    /// </summary>
    /// <param name="text">转义后的文本</param>
    /// <returns>原始文本</returns>
    public static string UnescapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        
        return text
            .Replace("[[", "[")
            .Replace("]]", "]");
    }
    
    /// <summary>
    /// 简化的 Markdown 格式化
    /// </summary>
    /// <param name="text">Markdown 文本</param>
    /// <returns>格式化后的 Markup</returns>
    private static string FormatMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        
        var escaped = EscapeMarkup(text);
        
        // 简化处理：代码块、行内代码、粗体、斜体
        var lines = escaped.Split('\n');
        var formattedLines = new List<string>();
        bool inCodeBlock = false;
        
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                formattedLines.Add(inCodeBlock ? "[dim]──────────[/]" : "[dim]──────────[/]");
                continue;
            }
            
            if (inCodeBlock)
            {
                formattedLines.Add($"[blue]{line}[/]");
                continue;
            }
            
            // 行内代码 `code`
            var formattedLine = line;
            formattedLine = System.Text.RegularExpressions.Regex.Replace(
                formattedLine, 
                @"`([^`]+)`", 
                "[blue]$1[/]");
            
            // 粗体 **text**
            formattedLine = System.Text.RegularExpressions.Regex.Replace(
                formattedLine,
                @"\*\*([^*]+)\*\*",
                "[bold]$1[/]");
            
            // 斜体 *text*（避免与粗体冲突）
            formattedLine = System.Text.RegularExpressions.Regex.Replace(
                formattedLine,
                @"(?<!\*)\*([^*]+)\*(?!\*)",
                "[italic]$1[/]");
            
            formattedLines.Add(formattedLine);
        }
        
        return string.Join("\n", formattedLines);
    }
    
    /// <summary>
    /// 获取状态对应的样式
    /// </summary>
    private static (string Icon, string Color) GetStatusStyle(ToolCallStatus status)
    {
        return status switch
        {
            ToolCallStatus.Pending => (ColorScheme.Icons.Pending, ColorScheme.PendingColor),
            ToolCallStatus.Running => (ColorScheme.Icons.Running, ColorScheme.RunningColor),
            ToolCallStatus.Success => (ColorScheme.Icons.Success, ColorScheme.SuccessColor),
            ToolCallStatus.Failed => (ColorScheme.Icons.Failed, ColorScheme.ErrorColor),
            ToolCallStatus.Rejected => (ColorScheme.Icons.Rejected, ColorScheme.ReasoningColor),
            _ => ("[white]?[/]", "white")
        };
    }
    
    /// <summary>
    /// 创建可折叠内容（使用 Collapsible 属性模拟）
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="content">内容</param>
    /// <param name="isExpanded">是否展开</param>
    /// <returns>渲染结果</returns>
    public IRenderable RenderCollapsible(string title, string content, bool isExpanded = false)
    {
        var icon = isExpanded ? ColorScheme.Icons.Expanded : ColorScheme.Icons.Folded;
        var escapedTitle = EscapeMarkup(title);
        var escapedContent = EscapeMarkup(content);
        
        if (isExpanded)
        {
            return new Rows(
                new Markup($"[{ColorScheme.ToolColor}]{icon} {escapedTitle}[/]"),
                new Panel(escapedContent)
                {
                    Border = BoxBorder.None,
                    Padding = new Padding(2, 0, 0, 0)
                });
        }
        else
        {
            return new Markup($"[{ColorScheme.ToolColor}]{icon} {escapedTitle}[/] [{ColorScheme.FoldHintColor}]（折叠）[/]");
        }
    }
    
    /// <summary>
    /// 渲染时间戳
    /// </summary>
    /// <param name="timestamp">时间戳</param>
    /// <returns>格式化的时间字符串</returns>
    public string RenderTimestamp(DateTime timestamp)
    {
        return $"[dim]{timestamp:HH:mm:ss}[/]";
    }
    
    /// <summary>
    /// 渲染分隔线
    /// </summary>
    /// <returns>分隔线渲染</returns>
    public IRenderable RenderSeparator()
    {
        return new Rule()
        {
            Style = new Style(Color.Grey)
        };
    }
}