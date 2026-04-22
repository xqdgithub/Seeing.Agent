using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.SpectreTui.Core;
using Seeing.Agent.SpectreTui.Core.State;
using Seeing.Agent.SpectreTui.Services;
using Seeing.Agent.SpectreTui.UI;

namespace Seeing.Agent.SpectreTui;

/// <summary>
/// 主应用入口 - Spectre.Console TUI 应用核心
/// 负责服务协调、生命周期管理、Live 显示循环和输入处理
/// </summary>
public class MainApp
{
    // ========== 核心服务 ==========

    private readonly LayoutService _layoutService;
    private readonly InputService _inputService;
    private readonly CommandPalette _commandPalette;
    private readonly AgentContext _agentContext;
    private readonly InputState _inputState;
    private readonly ILogger<MainApp> _logger;

    // ========== 运行状态 ==========

    /// <summary>应用是否正在运行</summary>
    private bool _isRunning = true;

    /// <summary>是否需要刷新显示</summary>
    private bool _needsRefresh = true;

    /// <summary>取消令牌源</summary>
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>刷新间隔（毫秒）</summary>
    private readonly int _refreshIntervalMs = LayoutConfig.StreamRefreshIntervalMs;

    /// <summary>
    /// 创建 MainApp 实例
    /// </summary>
    public MainApp(
        LayoutService layoutService,
        InputService inputService,
        CommandPalette commandPalette,
        AgentContext agentContext,
        InputState inputState,
        ILogger<MainApp> logger)
    {
        _layoutService = layoutService ?? throw new ArgumentNullException(nameof(layoutService));
        _inputService = inputService ?? throw new ArgumentNullException(nameof(inputService));
        _commandPalette = commandPalette ?? throw new ArgumentNullException(nameof(commandPalette));
        _agentContext = agentContext ?? throw new ArgumentNullException(nameof(agentContext));
        _inputState = inputState ?? throw new ArgumentNullException(nameof(inputState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 订阅输入服务事件
        SubscribeInputEvents();
    }

    /// <summary>
    /// 订阅输入服务的事件
    /// </summary>
    private void SubscribeInputEvents()
    {
        // 发送消息事件
        _inputService.OnSendMessage += HandleSendMessage;

        // 打开命令面板事件
        _inputService.OnOpenCommandPalette += HandleOpenCommandPalette;

        // 取消任务事件
        _inputService.OnCancelTask += HandleCancelTask;

        // 关闭面板事件
        _inputService.OnClosePanel += HandleClosePanel;

        // 输入状态变更事件
        _inputState.OnStateChanged += () => _needsRefresh = true;
    }

    /// <summary>
    /// 运行应用程序 - 主入口点
    /// 使用 AnsiConsole.Live 实现实时显示
    /// </summary>
    public async Task RunAsync()
    {
        _logger.LogInformation("Starting Spectre TUI application...");

        // 检查终端尺寸是否足够
        var width = AnsiConsole.Profile.Width;
        var height = AnsiConsole.Profile.Height;
        var minWidth = LayoutConfig.MinTerminalWidth;
        var minHeight = LayoutConfig.MinTerminalHeight;

        if (width < minWidth || height < minHeight)
        {
            AnsiConsole.MarkupLine($"[red]终端尺寸不足[/]");
            AnsiConsole.MarkupLine($"[yellow]当前: {width}x{height}[/]");
            AnsiConsole.MarkupLine($"[yellow]最小: {minWidth}x{minHeight}[/]");
            AnsiConsole.MarkupLine($"[dim]请扩大终端窗口后重试[/]");
            return;
        }

        // 初始化布局
        _layoutService.Initialize();

        // 显示欢迎信息
        ShowWelcome();

        try
        {
            // 启动 Live 显示循环
            await AnsiConsole.Live(_layoutService.Render())
                .StartAsync(async ctx =>
                {
                    // 主循环：处理输入和刷新显示
                    await MainLoopAsync(ctx);
                });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Application cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Application error");
            AnsiConsole.MarkupLine($"[red]应用错误: {EscapeMarkup(ex.Message)}[/]");
        }
    }

    /// <summary>
    /// 主循环 - 处理输入和刷新显示
    /// </summary>
    private async Task MainLoopAsync(LiveDisplayContext ctx)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastRefreshTime = 0L;

        while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            // 处理键盘输入
            var inputHandled = await _inputService.ProcessInputAsync(
                _refreshIntervalMs,
                _cancellationTokenSource.Token);

            // 判断是否需要刷新显示
            var timeSinceLastRefresh = stopwatch.ElapsedMilliseconds - lastRefreshTime;
            var shouldRefresh = _needsRefresh ||
                                 _layoutService.IsProcessing ||
                                 timeSinceLastRefresh >= _refreshIntervalMs;

            if (shouldRefresh)
            {
                // 更新布局内容
                _layoutService.Update();

                // 刷新显示
                ctx.Refresh();

                // 更新刷新时间
                lastRefreshTime = stopwatch.ElapsedMilliseconds;
                _needsRefresh = false;
            }

            // 等待一小段时间避免 CPU 占用过高
            if (!inputHandled)
            {
                await Task.Delay(10, _cancellationTokenSource.Token);
            }
        }

        _logger.LogInformation("Main loop ended");
    }

    // ========== 事件处理 ==========

    /// <summary>
    /// 处理发送消息事件
    /// </summary>
    private void HandleSendMessage(string input)
    {
        _logger.LogInformation("User input: {Input}", input);

        // 处理命令（以 / 开头）
        if (input.StartsWith('/'))
        {
            HandleCommand(input);
            return;
        }

        // 添加用户消息
        _layoutService.AddUserMessage(input);

        // 标记需要刷新
        _needsRefresh = true;

        // 模拟 AI 响应（后续集成 ChatOrchestrator）
        SimulateAiResponse(input);
    }

    /// <summary>
    /// 处理命令（/ 开头的输入）
    /// </summary>
    private void HandleCommand(string command)
    {
        var cmd = command.ToLowerInvariant().Trim();

        switch (cmd)
        {
            case "/help":
                ShowHelp();
                break;

            case "/exit":
            case "/quit":
                RequestStop();
                break;

            case "/clear":
                _layoutService.ClearMessages();
                _layoutService.AddSystemMessage("消息已清空");
                break;

            case "/status":
                ShowStatus();
                break;

            default:
                _layoutService.AddSystemMessage($"未知命令: {command}");
                break;
        }

        _needsRefresh = true;
    }

    /// <summary>
    /// 模拟 AI 响应（临时实现，后续集成 ChatOrchestrator）
    /// </summary>
    private void SimulateAiResponse(string input)
    {
        // 标记正在处理
        _layoutService.IsProcessing = true;
        _needsRefresh = true;

        // 启动后台任务处理响应
        Task.Run(async () =>
        {
            try
            {
                // 模拟处理延迟
                await Task.Delay(500, _cancellationTokenSource.Token);

                // 生成简单响应
                var response = GenerateMockResponse(input);

                // 模拟流式输出
                for (int i = 0; i < response.Length && !_cancellationTokenSource.Token.IsCancellationRequested; i += 5)
                {
                    var chunk = response.Substring(0, Math.Min(i + 5, response.Length));
                    _layoutService.UpdateStreamingContent(chunk);
                    _needsRefresh = true;
                    await Task.Delay(30, _cancellationTokenSource.Token);
                }

                // 完成流式输出
                _layoutService.CompleteStreaming();
            }
            catch (OperationCanceledException)
            {
                _layoutService.AddSystemMessage("[yellow]响应已取消[/]");
            }
            finally
            {
                _layoutService.IsProcessing = false;
                _needsRefresh = true;
            }
        }, _cancellationTokenSource.Token);
    }

    /// <summary>
    /// 生成模拟响应（测试用）
    /// </summary>
    private static string GenerateMockResponse(string input)
    {
        return $"收到您的消息: \"{input}\"。这是一个模拟响应，后续将集成 ChatOrchestrator 实现真实的 AI 对话功能。";
    }

    /// <summary>
    /// 处理打开命令面板事件
    /// </summary>
    private void HandleOpenCommandPalette()
    {
        _logger.LogInformation("Opening command palette");

        // 暂停 Live 显示，显示命令面板
        // Spectre.Console 不支持在 Live 中显示 SelectionPrompt
        // 需要先停止 Live，显示面板，然后重新启动

        // 临时解决方案：直接显示面板
        _commandPalette.Show();

        _needsRefresh = true;
    }

    /// <summary>
    /// 处理取消任务事件
    /// </summary>
    private void HandleCancelTask()
    {
        _logger.LogInformation("Cancel task requested");

        if (_layoutService.IsProcessing)
        {
            _cancellationTokenSource.Cancel();
            _layoutService.IsProcessing = false;
            _layoutService.AddSystemMessage("[yellow]任务已取消[/]");
            _needsRefresh = true;
        }
    }

    /// <summary>
    /// 处理关闭面板事件
    /// </summary>
    private void HandleClosePanel()
    {
        // 当前没有面板状态，预留接口
        _logger.LogInformation("Close panel requested");
    }

    // ========== 辅助方法 ==========

    /// <summary>
    /// 显示欢迎信息
    /// </summary>
    private void ShowWelcome()
    {
        _layoutService.AddSystemMessage("欢迎使用 Seeing.Agent Spectre TUI");
        _layoutService.AddSystemMessage("");
        _layoutService.AddSystemMessage("[dim]快捷键: Ctrl+P 打开命令面板, Enter 发送消息, Ctrl+C 取消任务[/]");
        _layoutService.AddSystemMessage("[dim]命令: /help 显示帮助, /exit 退出[/]");
    }

    /// <summary>
    /// 显示帮助信息
    /// </summary>
    private void ShowHelp()
    {
        _layoutService.AddSystemMessage("=== 帮助 ===");
        _layoutService.AddSystemMessage("[cyan]快捷键[/]");
        _layoutService.AddSystemMessage("  Ctrl+P  打开命令面板");
        _layoutService.AddSystemMessage("  Enter   发送消息（单行模式）");
        _layoutService.AddSystemMessage("  Ctrl+Enter 发送消息（多行模式）");
        _layoutService.AddSystemMessage("  Ctrl+C  取消当前任务");
        _layoutService.AddSystemMessage("  Ctrl+M  切换多行模式");
        _layoutService.AddSystemMessage("");
        _layoutService.AddSystemMessage("[cyan]命令[/]");
        _layoutService.AddSystemMessage("  /help   显示帮助");
        _layoutService.AddSystemMessage("  /exit   退出应用");
        _layoutService.AddSystemMessage("  /clear  清空消息");
        _layoutService.AddSystemMessage("  /status 显示状态");
    }

    /// <summary>
    /// 显示状态信息
    /// </summary>
    private void ShowStatus()
    {
        _layoutService.AddSystemMessage("=== 当前状态 ===");
        _layoutService.AddSystemMessage($"Agent: {_agentContext.CurrentAgentKey}");
        _layoutService.AddSystemMessage($"Model: {_agentContext.CurrentModel ?? "default"}");
        _layoutService.AddSystemMessage($"Messages: {_layoutService.MessageCount}");
        _layoutService.AddSystemMessage($"Tools: {_agentContext.ToolCount}");
        _layoutService.AddSystemMessage($"Skills: {_agentContext.SkillCount}");
        _layoutService.AddSystemMessage($"MCP Servers: {_agentContext.McpServerCount}");
    }

    /// <summary>
    /// 请求停止应用
    /// </summary>
    public void RequestStop()
    {
        _isRunning = false;
        _cancellationTokenSource.Cancel();
        _layoutService.AddSystemMessage("[green]正在退出...[/]");
        _needsRefresh = true;
    }

    /// <summary>
    /// 转义 Markup 特殊字符
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    /// <summary>
    /// 注册命令到命令面板
    /// </summary>
    public void RegisterCommands(IEnumerable<CommandItem> commands)
    {
        _commandPalette.RegisterCommands(commands);
    }
}