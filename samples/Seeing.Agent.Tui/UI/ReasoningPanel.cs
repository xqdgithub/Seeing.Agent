using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Tui.Core;

namespace Seeing.Agent.Tui.UI;

/// <summary>
/// 可折叠思考过程面板 - 显示 AI 推理过程
/// </summary>
/// <remarks>
/// 默认折叠保持界面整洁，展开后显示完整思考内容。
/// 使用灰色斜体样式，符合思考过程的视觉层次。
/// </remarks>
public class ReasoningPanel
{
    /// <summary>
    /// 思考内容
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// 是否展开显示详情
    /// </summary>
    public bool IsExpanded { get; set; }

    /// <summary>
    /// 唯一标识（用于批量渲染时跟踪展开状态）
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 创建思考过程面板
    /// </summary>
    /// <param name="content">思考内容</param>
    /// <param name="id">唯一标识（可选，默认自动生成）</param>
    /// <param name="isExpanded">是否默认展开</param>
    public ReasoningPanel(string content, string? id = null, bool isExpanded = false)
    {
        Content = content ?? string.Empty;
        Id = id ?? Guid.NewGuid().ToString("N")[..8];
        IsExpanded = isExpanded;
    }

    /// <summary>
    /// 渲染为 Spectre.Console 可渲染对象
    /// </summary>
    /// <returns>渲染结果</returns>
    public IRenderable Render()
    {
        return IsExpanded ? RenderExpanded() : RenderCollapsed();
    }

    /// <summary>
    /// 渲染折叠状态 - 显示摘要提示
    /// </summary>
    private IRenderable RenderCollapsed()
    {
        var foldIcon = ColorScheme.Icons.Folded;
        var reasoningIcon = ColorScheme.Icons.Reasoning;
        
        // 折叠提示：[图标] 思考过程 (点击展开)
        var headerMarkup = $"{foldIcon} {reasoningIcon} [{ColorScheme.ReasoningColor}]思考过程[/] [{ColorScheme.FoldHintColor}](点击展开)[/]";

        // 显示内容摘要（截取前50字符）
        var summary = GetContentSummary(50);
        if (!string.IsNullOrEmpty(summary))
        {
            var escapedSummary = EscapeMarkup(summary);
            headerMarkup += $" [{ColorScheme.FoldHintColor}]\"{escapedSummary}...\"[/]";
        }

        return new Markup(headerMarkup);
    }

    /// <summary>
    /// 渲染展开状态 - 显示完整内容
    /// </summary>
    private IRenderable RenderExpanded()
    {
        var rows = new List<IRenderable>();
        
        var expandedIcon = ColorScheme.Icons.Expanded;
        var reasoningIcon = ColorScheme.Icons.Reasoning;
        
        // 1. 标题行：▼ 💭 思考过程
        var headerMarkup = $"{expandedIcon} {reasoningIcon} [{ColorScheme.ReasoningColor} bold]思考过程[/]";
        rows.Add(new Markup(headerMarkup));

        // 2. 内容区域：灰色斜体
        if (!string.IsNullOrEmpty(Content))
        {
            var escapedContent = EscapeMarkup(Content);
            var contentMarkup = $"[{ColorScheme.ReasoningColor} italic]{escapedContent}[/]";
            
            // 使用 Panel 包装，无边框
            var contentPanel = new Panel(contentMarkup)
            {
                Border = BoxBorder.None,
                Padding = new Padding(2, 0, 1, 0),
                Expand = false
            };
            rows.Add(contentPanel);
        }

        // 3. 折叠提示（底部）
        rows.Add(new Markup($"[{ColorScheme.FoldHintColor}](点击折叠)[/]"));

        return new Rows(rows);
    }

    /// <summary>
    /// 获取内容摘要（用于折叠状态预览）
    /// </summary>
    /// <param name="maxLength">最大长度</param>
    /// <returns>截断后的摘要</returns>
    private string GetContentSummary(int maxLength)
    {
        if (string.IsNullOrEmpty(Content))
            return string.Empty;

        // 移除换行，获取第一行有效内容
        var firstLine = Content.Split('\n')[0].Trim();
        
        if (firstLine.Length <= maxLength)
            return firstLine;

        return firstLine.Substring(0, maxLength);
    }

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    /// <param name="text">原始文本</param>
    /// <returns>转义后的安全文本</returns>
    private static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("[", "[[")
            .Replace("]", "]]");
    }

    /// <summary>
    /// 切换展开/折叠状态
    /// </summary>
    public void Toggle()
    {
        IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// 批量渲染思考过程列表
    /// </summary>
    /// <param name="contents">思考内容列表</param>
    /// <param name="expandedIds">已展开的 ID 集合</param>
    /// <returns>渲染结果集合</returns>
    public static IEnumerable<IRenderable> RenderMultiple(
        IEnumerable<string> contents,
        HashSet<string>? expandedIds = null)
    {
        expandedIds ??= new HashSet<string>();

        foreach (var content in contents)
        {
            var panel = new ReasoningPanel(content);
            panel.IsExpanded = expandedIds.Contains(panel.Id);
            yield return panel.Render();
        }
    }

    /// <summary>
    /// 批量渲染思考过程面板列表
    /// </summary>
    /// <param name="panels">面板列表</param>
    /// <param name="expandedIds">已展开的 ID 集合</param>
    /// <returns>渲染结果集合</returns>
    public static IEnumerable<IRenderable> RenderMultiple(
        IEnumerable<ReasoningPanel> panels,
        HashSet<string>? expandedIds = null)
    {
        expandedIds ??= new HashSet<string>();

        foreach (var panel in panels)
        {
            panel.IsExpanded = expandedIds.Contains(panel.Id);
            yield return panel.Render();
        }
    }
}