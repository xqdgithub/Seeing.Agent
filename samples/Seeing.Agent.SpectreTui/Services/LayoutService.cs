using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.SpectreTui.Core;
using Seeing.Agent.SpectreTui.Core.State;
using Seeing.Agent.SpectreTui.UI;

namespace Seeing.Agent.SpectreTui.Services;

/// <summary>
/// 布局服务 - 使用 Spectre.Console Layout 创建两栏布局
/// 布局结构: Header(顶部) → Messages(消息区) → Input(输入区) → Footer(状态栏)
/// </summary>
public class LayoutService
{
    private readonly AgentContext _agentContext;
    private readonly InputState _inputState;
    private readonly InputBox _inputBox;
    private readonly StatusBar _statusBar;

    /// <summary>根布局</summary>
    private Layout? _rootLayout;

    /// <summary>消息列表（用于显示）</summary>
    private readonly List<string> _messages = new();

    /// <summary>流式输出缓冲</summary>
    private string _streamingContent = string.Empty;

    /// <summary>是否正在处理</summary>
    public bool IsProcessing { get; set; }

    /// <summary>
    /// 创建 LayoutService 实例
    /// </summary>
    public LayoutService(
        AgentContext agentContext,
        InputState inputState,
        InputBox inputBox,
        StatusBar statusBar)
    {
        _agentContext = agentContext;
        _inputState = inputState;
        _inputBox = inputBox;
        _statusBar = statusBar;
    }

    /// <summary>
    /// 初始化布局结构
    /// </summary>
    public void Initialize()
    {
        _rootLayout = CreateLayout();
    }

    /// <summary>
    /// 创建两栏布局结构
    /// </summary>
    private Layout CreateLayout()
    {
        // 简化布局：去掉不必要的 SplitColumns 嵌套，直接使用 SplitRows
        // Oracle 建议：当只有一个列时，不需要 SplitColumns
        
        var headerLayout = new Layout(LayoutRegions.Header)
            .Size(LayoutConfig.HeaderSize);

        var messagesLayout = new Layout(LayoutRegions.Messages)
            .Ratio(LayoutConfig.MessageAreaRatio);

        var inputLayout = new Layout(LayoutRegions.Input)
            .Size(LayoutConfig.InputAreaSize);

        var footerLayout = new Layout(LayoutRegions.Footer)
            .Size(LayoutConfig.StatusBarSize);

        // 直接 SplitRows，不需要 SplitColumns 包装
        return new Layout(LayoutRegions.Root)
            .SplitRows(
                headerLayout,
                messagesLayout,
                inputLayout,
                footerLayout
            );
    }

    /// <summary>
    /// 更新布局内容
    /// </summary>
    public void Update()
    {
        if (_rootLayout == null)
            return;

        // 更新头部
        UpdateHeader();

        // 更新消息区域
        UpdateMessages();

        // 更新输入区域
        UpdateInput();

        // 更新状态栏
        UpdateFooter();
    }

    /// <summary>
    /// 更新头部区域
    /// </summary>
    private void UpdateHeader()
    {
        var headerLayout = _rootLayout?[LayoutRegions.Header];
        if (headerLayout == null) return;

        // 简化：直接用 Markup，不用 Panel
        var headerContent = new Markup("[bold white on blue] Seeing.Agent Spectre TUI [/]");

        headerLayout.Update(headerContent);
    }

    /// <summary>
    /// 更新消息区域
    /// </summary>
    private void UpdateMessages()
    {
        var messagesLayout = _rootLayout?[LayoutRegions.Messages];
        if (messagesLayout == null) return;

        // 构建消息内容
        var rows = new List<IRenderable>();

        // 显示历史消息
        foreach (var msg in _messages.TakeLast(LayoutConfig.MaxMessageHistory))
        {
            rows.Add(new Markup(msg));
        }

        // 显示流式输出（如果有）
        if (!string.IsNullOrEmpty(_streamingContent))
        {
            rows.Add(new Markup($"[blue]AI: {_streamingContent}[/]"));
        }

        // 处理状态指示
        if (IsProcessing)
        {
            rows.Add(new Markup($"[{ColorScheme.ToolRunningColor}]⏳ 处理中...[/]"));
        }

        // 如果没有消息，显示欢迎信息
        if (rows.Count == 0)
        {
            rows.Add(new Markup("[dim]欢迎使用 Seeing.Agent[/]"));
            rows.Add(new Markup(""));
            rows.Add(new Markup("[dim]按 Ctrl+P 打开命令面板[/]"));
            rows.Add(new Markup("[dim]输入消息后按 Enter 发送[/]"));
        }

        // 简化 Panel：去掉 Header 和复杂 Padding，避免尺寸问题
        var panel = new Panel(new Rows(rows))
        {
            Border = BoxBorder.None,  // 去掉边框减少尺寸消耗
            Expand = true
        };

        messagesLayout.Update(panel);
    }

    /// <summary>
    /// 更新输入区域
    /// </summary>
    private void UpdateInput()
    {
        var inputLayout = _rootLayout?[LayoutRegions.Input];
        if (inputLayout == null) return;

        // Oracle 建议：不手动设置 Panel.Width，让 Layout 自动管理
        var inputPanel = _inputBox.RenderPanel();  // 不传递宽度参数
        inputLayout.Update(inputPanel);
    }

    /// <summary>
    /// 更新状态栏
    /// </summary>
    private void UpdateFooter()
    {
        var footerLayout = _rootLayout?[LayoutRegions.Footer];
        if (footerLayout == null) return;

        // 直接使用 Markup，不额外包装
        var statusMarkup = _statusBar.RenderMarkup();
        footerLayout.Update(statusMarkup);
    }

    /// <summary>
    /// 获取渲染后的布局（供 AnsiConsole.Write 使用）
    /// </summary>
    public IRenderable Render()
    {
        Update();
        return _rootLayout ?? (IRenderable)new Text("布局未初始化");
    }

    // ========== 消息管理 ==========

    /// <summary>
    /// 添加用户消息
    /// </summary>
    public void AddUserMessage(string content)
    {
        var escapedContent = EscapeMarkup(content);
        _messages.Add($"[{ColorScheme.UserMessageColor}]用户: {escapedContent}[/]");
        _agentContext.MessageCount = _messages.Count;
    }

    /// <summary>
    /// 添加助手消息
    /// </summary>
    public void AddAssistantMessage(string content)
    {
        var escapedContent = EscapeMarkup(content);
        _messages.Add($"[{ColorScheme.AssistantMessageColor}]AI: {escapedContent}[/]");
        _agentContext.MessageCount = _messages.Count;
    }

    /// <summary>
    /// 添加系统消息
    /// </summary>
    public void AddSystemMessage(string content)
    {
        var escapedContent = EscapeMarkup(content);
        _messages.Add($"[{ColorScheme.SystemMessageColor}]系统: {escapedContent}[/]");
        _agentContext.MessageCount = _messages.Count;
    }

    /// <summary>
    /// 添加错误消息
    /// </summary>
    public void AddErrorMessage(string content)
    {
        var escapedContent = EscapeMarkup(content);
        _messages.Add($"[{ColorScheme.ErrorColor}]错误: {escapedContent}[/]");
        _agentContext.MessageCount = _messages.Count;
    }

    /// <summary>
    /// 更新流式输出内容
    /// </summary>
    public void UpdateStreamingContent(string content)
    {
        _streamingContent = EscapeMarkup(content);
    }

    /// <summary>
    /// 完成流式输出，将内容保存为消息
    /// </summary>
    public void CompleteStreaming()
    {
        if (!string.IsNullOrEmpty(_streamingContent))
        {
            AddAssistantMessage(_streamingContent);
            _streamingContent = string.Empty;
        }
    }

    /// <summary>
    /// 清空消息历史
    /// </summary>
    public void ClearMessages()
    {
        _messages.Clear();
        _agentContext.MessageCount = 0;
    }

    /// <summary>
    /// 获取消息数量
    /// </summary>
    public int MessageCount => _messages.Count;

    // ========== 工具方法 ==========

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}