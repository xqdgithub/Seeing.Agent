using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Models;
using Seeing.Agent.Tui.Core.State;
using Seeing.Agent.Tui.Services;

// 解决 TuiState 命名冲突（Core.TuiState vs State.TuiState）
using TuiStateBase = Seeing.Agent.Tui.Core.State.TuiState;

namespace Seeing.Agent.Tui.UI;

/// <summary>
/// 消息历史渲染面板 - 使用 Panel + Rows 组合显示消息历史
/// </summary>
public sealed class MessagePanel
{
    private readonly TuiStateBase _state;
    private readonly RenderService _renderService;
    
    /// <summary>每条消息预估高度（用于虚拟滚动计算）</summary>
    private const int EstimatedMessageHeight = 4;
    
    /// <summary>面板边框和头部占用的高度</summary>
    private const int PanelOverhead = 6;
    
    /// <summary>最大单条消息显示长度</summary>
    private const int MaxMessageLength = 500;

    /// <summary>
    /// 构造消息面板
    /// </summary>
    /// <param name="state">TUI 状态</param>
    /// <param name="renderService">渲染服务</param>
    public MessagePanel(TuiStateBase state, RenderService renderService)
    {
        _state = state;
        _renderService = renderService;
    }

    /// <summary>
    /// 渲染消息历史面板（虚拟滚动 + 搜索高亮）
    /// </summary>
    /// <returns>渲染后的 Panel</returns>
    public Panel Render()
    {
        var messages = _state.Messages.GetSnapshot();
        if (messages.Count == 0)
        {
            return RenderEmptyPanel();
        }

        // 计算可见消息范围（虚拟滚动）
        var (startIndex, endIndex) = CalculateVisibleRange(messages.Count);
        
        // 渲染可见消息
        var visibleMessages = new List<IRenderable>();
        for (var i = startIndex; i <= endIndex && i < messages.Count; i++)
        {
            var msg = messages[i];
            var isHighlighted = _state.Navigation.HighlightedMessageIndex == i;
            var isSearchMatch = _state.Navigation.SearchMatchIndices.Contains(i);
            
            visibleMessages.Add(RenderMessageRow(msg, i, isHighlighted, isSearchMatch));
        }

        // 构建面板
        var content = new Rows(visibleMessages);
        var panel = new Panel(content)
        {
            Header = new PanelHeader(BuildPanelHeader(messages.Count, startIndex, endIndex)),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(0, 1),
            Expand = true
        };

        return panel;
    }

    /// <summary>
    /// 渲染空消息面板
    /// </summary>
    private Panel RenderEmptyPanel()
    {
        var welcomeContent = new Rows(
            new Markup($"[{ColorScheme.SystemColor}]欢迎使用 Seeing.Agent[/]"),
            new Markup($"[{ColorScheme.FoldHintColor}]输入消息开始对话...[/]")
        );
        
        return new Panel(welcomeContent)
        {
            Header = new PanelHeader("消息"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 1),
            Expand = true
        };
    }

    /// <summary>
    /// 计算可见消息范围（虚拟滚动）
    /// </summary>
    /// <param name="totalCount">消息总数</param>
    /// <returns>(startIndex, endIndex) 可见范围</returns>
    private (int StartIndex, int EndIndex) CalculateVisibleRange(int totalCount)
    {
        // 可用高度 = 终端高度 - 面板开销
        var availableHeight = Math.Max(10, _state.TerminalHeight - PanelOverhead);
        
        // 可见消息数量 = 可用高度 / 每条消息预估高度
        var visibleCount = Math.Max(1, availableHeight / EstimatedMessageHeight);
        
        // 计算起始索引（从滚动偏移开始）
        var startIndex = Math.Max(0, Math.Min(_state.ScrollOffset, totalCount - visibleCount));
        
        // 计算结束索引
        var endIndex = Math.Min(startIndex + visibleCount - 1, totalCount - 1);
        
        return (startIndex, endIndex);
    }

    /// <summary>
    /// 渲染单条消息行（角色图标 + 时间戳 + 内容）
    /// </summary>
    /// <param name="msg">消息模型</param>
    /// <param name="index">消息索引</param>
    /// <param name="isHighlighted">是否高亮</param>
    /// <param name="isSearchMatch">是否搜索匹配</param>
    /// <returns>渲染结果</returns>
    private IRenderable RenderMessageRow(MessageDisplay msg, int index, bool isHighlighted, bool isSearchMatch)
    {
        var roleIcon = GetRoleIcon(msg.Role);
        var timestamp = _renderService.RenderTimestamp(msg.Timestamp);
        
        // 处理内容（截断 + 搜索高亮）
        var content = TruncateAndHighlightContent(msg.Content, isSearchMatch);
        
        // 构建消息行
        var rowContent = $"{roleIcon} {timestamp} {content}";
        
        // 高亮样式
        var rowMarkup = isHighlighted 
            ? $"[invert]{rowContent}[/]"
            : rowContent;

        return new Markup(rowMarkup);
    }

    /// <summary>
    /// 获取角色图标
    /// </summary>
    private static string GetRoleIcon(string role) => role switch
    {
        "user" => ColorScheme.Icons.User,
        "assistant" => ColorScheme.Icons.Assistant,
        "tool" => ColorScheme.Icons.Tool,
        "system" => $"[{ColorScheme.SystemColor}]⚙[/]",
        _ => $"[{ColorScheme.SystemColor}]•[/]"
    };

    /// <summary>
    /// 截断内容并添加搜索高亮
    /// </summary>
    /// <param name="content">原始内容</param>
    /// <param name="isSearchMatch">是否搜索匹配</param>
    /// <returns>处理后的 Markup 内容</returns>
    private string TruncateAndHighlightContent(string content, bool isSearchMatch)
    {
        // 转义特殊字符
        var escaped = RenderService.EscapeMarkup(content);
        
        // 截断长消息
        if (escaped.Length > MaxMessageLength)
        {
            escaped = escaped.Substring(0, MaxMessageLength) + "...";
        }

        // 搜索高亮
        if (isSearchMatch && !string.IsNullOrEmpty(_state.Navigation.SearchKeyword))
        {
            escaped = HighlightKeyword(escaped, _state.Navigation.SearchKeyword);
        }

        return escaped;
    }

    /// <summary>
    /// 高亮关键词
    /// </summary>
    /// <param name="text">已转义的文本</param>
    /// <param name="keyword">搜索关键词</param>
    /// <returns>带高亮的 Markup</returns>
    private static string HighlightKeyword(string text, string keyword)
    {
        if (string.IsNullOrEmpty(keyword))
            return text;
        
        // 转义关键词（防止破坏 Markup）
        var escapedKeyword = RenderService.EscapeMarkup(keyword);
        
        // 使用黄色背景高亮关键词
        // 注意：由于已转义，直接替换文本
        return text.Replace(
            escapedKeyword,
            $"[yellow on black]{escapedKeyword}[/]",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构建面板头部文本
    /// </summary>
    /// <param name="totalCount">消息总数</param>
    /// <param name="startIndex">起始索引</param>
    /// <param name="endIndex">结束索引</param>
    /// <returns>头部文本</returns>
    private string BuildPanelHeader(int totalCount, int startIndex, int endIndex)
    {
        var baseHeader = $"消息 ({totalCount})";
        
        // 显示虚拟滚动范围
        if (totalCount > 0 && endIndex < totalCount - 1)
        {
            baseHeader += $" [{ColorScheme.FoldHintColor}]({startIndex + 1}-{endIndex + 1})[/]";
        }
        
        // 搜索匹配数
        if (_state.Navigation.IsSearchMode && _state.Navigation.SearchMatchIndices.Count > 0)
        {
            var matchCount = _state.Navigation.SearchMatchIndices.Count;
            var currentIdx = _state.Navigation.CurrentSearchMatchIndex + 1;
            baseHeader += $" [{ColorScheme.WarningColor}]{matchCount} 匹配 ({currentIdx}/{matchCount})[/]";
        }
        
        return baseHeader;
    }

    /// <summary>
    /// 渲染完整消息详情（用于展开查看）
    /// </summary>
    /// <param name="msg">消息模型</param>
    /// <param name="expandTools">是否展开工具调用</param>
    /// <param name="expandReasoning">是否展开思考过程</param>
    /// <returns>渲染结果集合</returns>
    public IEnumerable<IRenderable> RenderMessageDetail(
        MessageDisplay msg, 
        bool expandTools = false, 
        bool expandReasoning = false)
    {
        return _renderService.RenderMessage(msg, expandTools, expandReasoning);
    }

    /// <summary>
    /// 计算滚动到指定消息所需的偏移
    /// </summary>
    /// <param name="targetIndex">目标消息索引</param>
    /// <param name="totalCount">消息总数</param>
    /// <returns>滚动偏移值</returns>
    public int CalculateScrollOffset(int targetIndex, int totalCount)
    {
        var availableHeight = Math.Max(10, _state.TerminalHeight - PanelOverhead);
        var visibleCount = Math.Max(1, availableHeight / EstimatedMessageHeight);
        
        // 目标消息在可见区域的中间位置
        var desiredOffset = targetIndex - visibleCount / 2;
        
        // 边界检查
        return Math.Max(0, Math.Min(desiredOffset, totalCount - visibleCount));
    }
}