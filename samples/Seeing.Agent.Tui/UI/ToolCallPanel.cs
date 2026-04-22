using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.Json;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Models;
using Seeing.Agent.Core.Events;

namespace Seeing.Agent.Tui.UI;

/// <summary>
/// 可折叠工具调用面板 - 显示工具执行状态和结果
/// </summary>
public class ToolCallPanel
{
    /// <summary>
    /// 工具调用显示模型
    /// </summary>
    public ToolCallDisplay ToolCall { get; }

    /// <summary>
    /// 是否展开显示详情
    /// </summary>
    public bool IsExpanded { get; set; }

    /// <summary>
    /// 创建工具调用面板
    /// </summary>
    /// <param name="toolCall">工具调用模型</param>
    /// <param name="isExpanded">是否默认展开</param>
    public ToolCallPanel(ToolCallDisplay toolCall, bool isExpanded = false)
    {
        ToolCall = toolCall;
        IsExpanded = isExpanded;
    }

    /// <summary>
    /// 渲染为 Spectre.Console 可渲染对象
    /// </summary>
    /// <returns>渲染结果</returns>
    public IRenderable Render()
    {
        // 紧凑模式：只显示状态图标 + 工具名称
        if (!IsExpanded)
        {
            return RenderCollapsed();
        }

        // 展开模式：显示完整详情
        return RenderExpanded();
    }

    /// <summary>
    /// 渲染折叠状态 - 紧凑显示
    /// </summary>
    private IRenderable RenderCollapsed()
    {
        var icon = GetStatusIcon();
        var color = GetStatusColor();
        var escapedName = EscapeMarkup(ToolCall.Name);

        // 标题行：[图标] tool_name
        var headerMarkup = $"{icon} [{color}]{escapedName}[/]";

        // 添加执行时间（如果有）
        if (ToolCall.Duration.HasValue)
        {
            var durationMs = ToolCall.Duration.Value.TotalMilliseconds;
            headerMarkup += $" [dim]({durationMs:0}ms)[/]";
        }

        // 失败时简要显示错误
        if (ToolCall.Status == ToolCallStatus.Failed && !string.IsNullOrEmpty(ToolCall.Error))
        {
            var escapedError = EscapeMarkup(TruncateText(ToolCall.Error, 50));
            headerMarkup += $" [red]{escapedError}[/]";
        }

        var panel = new Panel(headerMarkup)
        {
            Border = BoxBorder.Square,
            BorderStyle = new Style(GetSpectreColor(ToolCall.Status)),
            Padding = new Padding(1, 0),
            Expand = false
        };

        return panel;
    }

    /// <summary>
    /// 渲染展开状态 - 显示完整详情
    /// </summary>
    private IRenderable RenderExpanded()
    {
        var rows = new List<IRenderable>();

        // 1. 标题行：状态图标 + 工具名称 + 耗时
        var headerMarkup = BuildHeaderLine();
        rows.Add(new Markup(headerMarkup));

        // 2. 参数区域（JSON 格式化）
        if (!string.IsNullOrEmpty(ToolCall.Arguments))
        {
            rows.Add(new Markup("[dim]───────── 参数 ─────────[/]"));
            rows.Add(RenderFormattedJson(ToolCall.Arguments, ColorScheme.ToolColor));
        }

        // 3. 结果区域（如果有）
        if (!string.IsNullOrEmpty(ToolCall.Result))
        {
            rows.Add(new Markup("[dim]───────── 结果 ─────────[/]"));
            rows.Add(RenderFormattedJson(ToolCall.Result, ColorScheme.SuccessColor));
        }

        // 4. 错误区域（如果有）
        if (!string.IsNullOrEmpty(ToolCall.Error))
        {
            rows.Add(new Markup("[dim]───────── 错误 ─────────[/]"));
            rows.Add(new Markup($"[{ColorScheme.ErrorColor}]{EscapeMarkup(ToolCall.Error)}[/]"));
        }

        // 5. 执行时间
        if (ToolCall.Duration.HasValue)
        {
            var durationMs = ToolCall.Duration.Value.TotalMilliseconds;
            rows.Add(new Markup($"[dim]耗时: {durationMs:0.00}ms[/]"));
        }

        return new Rows(rows);
    }

    /// <summary>
    /// 构建标题行
    /// </summary>
    private string BuildHeaderLine()
    {
        var icon = GetStatusIcon();
        var color = GetStatusColor();
        var escapedName = EscapeMarkup(ToolCall.Name);
        var foldIcon = ColorScheme.Icons.Expanded;

        var header = $"{foldIcon} {icon} [{color} bold]{escapedName}[/]";

        if (ToolCall.Duration.HasValue)
        {
            header += $" [dim]({ToolCall.Duration.Value.TotalMilliseconds:0}ms)[/]";
        }

        return header;
    }

    /// <summary>
    /// 获取状态图标
    /// </summary>
    private string GetStatusIcon()
    {
        return ToolCall.Status switch
        {
            ToolCallStatus.Pending => ColorScheme.Icons.Pending,
            ToolCallStatus.Running => ColorScheme.Icons.Running,
            ToolCallStatus.Success => ColorScheme.Icons.Success,
            ToolCallStatus.Failed => ColorScheme.Icons.Failed,
            ToolCallStatus.Rejected => ColorScheme.Icons.Rejected,
            _ => "[grey]?[/]"
        };
    }

    /// <summary>
    /// 获取状态颜色（字符串格式）
    /// </summary>
    private string GetStatusColor()
    {
        return ToolCall.Status switch
        {
            ToolCallStatus.Pending => ColorScheme.PendingColor,
            ToolCallStatus.Running => ColorScheme.RunningColor,
            ToolCallStatus.Success => ColorScheme.SuccessColor,
            ToolCallStatus.Failed => ColorScheme.ErrorColor,
            ToolCallStatus.Rejected => ColorScheme.ReasoningColor,
            _ => "white"
        };
    }

    /// <summary>
    /// 获取 Spectre.Console Color 对象
    /// </summary>
    private Color GetSpectreColor(ToolCallStatus status)
    {
        return status switch
        {
            ToolCallStatus.Pending => Color.Yellow,
            ToolCallStatus.Running => Color.Blue,
            ToolCallStatus.Success => Color.Green,
            ToolCallStatus.Failed => Color.Red,
            ToolCallStatus.Rejected => Color.Grey,
            _ => Color.White
        };
    }

    /// <summary>
    /// 格式化 JSON 内容为可渲染对象
    /// </summary>
    /// <param name="json">JSON 字符串</param>
    /// <param name="color">显示颜色</param>
    /// <returns>渲染结果</returns>
    private IRenderable RenderFormattedJson(string json, string color)
    {
        try
        {
            // 尝试解析并美化 JSON
            var parsed = JsonDocument.Parse(json);
            var formatted = JsonSerializer.Serialize(parsed, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            var escaped = EscapeMarkup(formatted);
            return new Markup($"[{color}]{escaped}[/]");
        }
        catch (JsonException)
        {
            // JSON 解析失败，直接显示原始内容
            var escaped = EscapeMarkup(json);
            return new Markup($"[{color}]{escaped}[/]");
        }
    }

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("[", "[[")
            .Replace("]", "]]");
    }

    /// <summary>
    /// 截断文本到指定长度
    /// </summary>
    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength) + "...";
    }

    /// <summary>
    /// 切换展开/折叠状态
    /// </summary>
    public void Toggle()
    {
        IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// 更新工具调用状态（用于实时更新）
    /// </summary>
    /// <param name="status">新状态</param>
    /// <param name="result">执行结果</param>
    /// <param name="error">错误信息</param>
    public void UpdateStatus(ToolCallStatus status, string? result = null, string? error = null)
    {
        ToolCall.Status = status;
        
        if (result != null)
            ToolCall.Result = result;
        
        if (error != null)
            ToolCall.Error = error;

        if (status == ToolCallStatus.Success || status == ToolCallStatus.Failed || status == ToolCallStatus.Rejected)
        {
            ToolCall.EndTime = DateTime.Now;
        }
    }

    /// <summary>
    /// 批量渲染工具调用列表
    /// </summary>
    /// <param name="toolCalls">工具调用列表</param>
    /// <param name="expandedIds">已展开的 ID 集合</param>
    /// <returns>渲染结果集合</returns>
    public static IEnumerable<IRenderable> RenderMultiple(
        IEnumerable<ToolCallDisplay> toolCalls,
        HashSet<string>? expandedIds = null)
    {
        expandedIds ??= new HashSet<string>();

        foreach (var tc in toolCalls)
        {
            var isExpanded = expandedIds.Contains(tc.Id);
            var panel = new ToolCallPanel(tc, isExpanded);
            yield return panel.Render();
        }
    }
}