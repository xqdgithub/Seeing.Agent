using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Models;

namespace Seeing.Agent.Tui.UI;

/// <summary>
/// 子代理状态面板 - 显示子代理启动和完成状态
/// </summary>
/// <remarks>
/// 子代理状态通常短暂，简洁显示即可。
/// 使用颜色区分状态：启动中黄色，完成绿色，失败红色。
/// </remarks>
public class SubAgentPanel
{
    /// <summary>
    /// 子代理显示模型
    /// </summary>
    public SubAgentDisplay SubAgent { get; }

    /// <summary>
    /// 是否显示跳转提示（可选功能）
    /// </summary>
    public bool ShowJumpHint { get; set; }

    /// <summary>
    /// 创建子代理状态面板
    /// </summary>
    /// <param name="subAgent">子代理模型</param>
    /// <param name="showJumpHint">是否显示跳转提示</param>
    public SubAgentPanel(SubAgentDisplay subAgent, bool showJumpHint = false)
    {
        SubAgent = subAgent;
        ShowJumpHint = showJumpHint;
    }

    /// <summary>
    /// 渲染为 Spectre.Console 可渲染对象
    /// </summary>
    /// <returns>渲染结果</returns>
    public IRenderable Render()
    {
        var icon = GetStatusIcon();
        var color = GetStatusColor();
        var escapedName = EscapeMarkup(SubAgent.Name);
        var timestamp = SubAgent.StartTime.ToString("HH:mm:ss");

        // 主行：[子代理: researcher] 启动中...
        // 或：[子代理: researcher] 完成 ✓
        var statusText = GetStatusText();
        var headerMarkup = $"[{color}][子代理: {escapedName}][/] {icon} {statusText} [dim]{timestamp}[/]";

        // 完成时显示耗时
        if (SubAgent.Duration.HasValue)
        {
            var durationSec = SubAgent.Duration.Value.TotalSeconds;
            headerMarkup += $" [dim]({durationSec:0.0}s)[/]";
        }

        // 可选：显示跳转提示
        if (ShowJumpHint && !string.IsNullOrEmpty(SubAgent.SubSessionId))
        {
            headerMarkup += $" [dim](点击跳转)[/]";
        }

        // 失败时简要显示错误
        if (SubAgent.Status == SubAgentStatus.Failed && !string.IsNullOrEmpty(SubAgent.Error))
        {
            var escapedError = EscapeMarkup(TruncateText(SubAgent.Error, 50));
            headerMarkup += $" [red]{escapedError}[/]";
        }

        var panel = new Panel(headerMarkup)
        {
            Border = BoxBorder.Square,
            BorderStyle = new Style(GetSpectreColor(SubAgent.Status)),
            Padding = new Padding(1, 0),
            Expand = false
        };

        return panel;
    }

    /// <summary>
    /// 获取状态图标
    /// </summary>
    private string GetStatusIcon()
    {
        return SubAgent.Status switch
        {
            SubAgentStatus.Starting => ColorScheme.Icons.Pending,
            SubAgentStatus.Running => ColorScheme.Icons.Running,
            SubAgentStatus.Completed => ColorScheme.Icons.Success,
            SubAgentStatus.Failed => ColorScheme.Icons.Failed,
            _ => "[grey]?[/]"
        };
    }

    /// <summary>
    /// 获取状态颜色（字符串格式）
    /// </summary>
    private string GetStatusColor()
    {
        return SubAgent.Status switch
        {
            SubAgentStatus.Starting => ColorScheme.PendingColor,
            SubAgentStatus.Running => ColorScheme.RunningColor,
            SubAgentStatus.Completed => ColorScheme.SuccessColor,
            SubAgentStatus.Failed => ColorScheme.ErrorColor,
            _ => "white"
        };
    }

    /// <summary>
    /// 获取 Spectre.Console Color 对象
    /// </summary>
    private Color GetSpectreColor(SubAgentStatus status)
    {
        return status switch
        {
            SubAgentStatus.Starting => Color.Yellow,
            SubAgentStatus.Running => Color.Blue,
            SubAgentStatus.Completed => Color.Green,
            SubAgentStatus.Failed => Color.Red,
            _ => Color.White
        };
    }

    /// <summary>
    /// 获取状态文本
    /// </summary>
    private string GetStatusText()
    {
        return SubAgent.Status switch
        {
            SubAgentStatus.Starting => "启动中...",
            SubAgentStatus.Running => "运行中...",
            SubAgentStatus.Completed => "完成 ✓",
            SubAgentStatus.Failed => "失败 ✗",
            _ => "未知"
        };
    }

    /// <summary>
    /// 更新子代理状态（用于实时更新）
    /// </summary>
    /// <param name="status">新状态</param>
    /// <param name="result">执行结果</param>
    /// <param name="error">错误信息</param>
    public void UpdateStatus(SubAgentStatus status, string? result = null, string? error = null)
    {
        SubAgent.Status = status;
        
        if (result != null)
            SubAgent.Result = result;
        
        if (error != null)
            SubAgent.Error = error;

        if (status == SubAgentStatus.Completed || status == SubAgentStatus.Failed)
        {
            SubAgent.EndTime = DateTime.Now;
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
    /// 批量渲染子代理列表
    /// </summary>
    /// <param name="subAgents">子代理列表</param>
    /// <param name="showJumpHint">是否显示跳转提示</param>
    /// <returns>渲染结果集合</returns>
    public static IEnumerable<IRenderable> RenderMultiple(
        IEnumerable<SubAgentDisplay> subAgents,
        bool showJumpHint = false)
    {
        foreach (var sa in subAgents)
        {
            var panel = new SubAgentPanel(sa, showJumpHint);
            yield return panel.Render();
        }
    }

    /// <summary>
    /// 从 SubAgentEvent 创建显示模型
    /// </summary>
    /// <param name="subAgentEvent">子代理事件</param>
    /// <returns>显示模型</returns>
    public static SubAgentDisplay FromEvent(Seeing.Agent.Core.Events.SubAgentEvent subAgentEvent)
    {
        var status = subAgentEvent.Status switch
        {
            "started" => SubAgentStatus.Running,
            "completed" => SubAgentStatus.Completed,
            "failed" => SubAgentStatus.Failed,
            _ => SubAgentStatus.Starting
        };

        return new SubAgentDisplay
        {
            Name = subAgentEvent.AgentName,
            Status = status,
            SubSessionId = subAgentEvent.SubSessionId,
            StartTime = subAgentEvent.Timestamp,
            EndTime = status == SubAgentStatus.Completed || status == SubAgentStatus.Failed 
                ? subAgentEvent.Timestamp 
                : null,
            Result = subAgentEvent.Result,
            Error = subAgentEvent.Error
        };
    }
}