using Spectre.Console;
using Spectre.Console.Rendering;
using Seeing.Agent.Tui.Core.State;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.UI.Renderers;

/// <summary>
/// Live 渲染器 - 在 Spectre.Console Live 上下文中实时渲染 UI
/// </summary>
/// <remarks>
/// 设计目标：
/// 1. 使用 AnsiConsole.Live 创建实时更新的显示区域
/// 2. 集成 EventRouter 实现事件驱动的实时刷新
/// 3. 支持取消令牌，优雅退出
/// 4. 低刷新频率（100ms），避免 CPU 过载
/// </remarks>
public sealed class LiveRenderer
{
    private readonly EventRouter _router;
    private readonly TuiState _state;
    
    /// <summary>是否正在运行</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 构造 Live 渲染器
    /// </summary>
    /// <param name="router">事件路由器</param>
    /// <param name="state">TUI 状态</param>
    public LiveRenderer(EventRouter router, TuiState state)
    {
        _router = router;
        _state = state;
    }

    /// <summary>
    /// 启动 Live 渲染循环
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <remarks>
    /// 此方法创建一个 Live 显示区域，在其中运行 EventRouter。
    /// EventRouter 会持续消费 Channel 并调用 ctx.Refresh() 刷新显示。
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;

        try
        {
            // 创建初始渲染目标
            var initialContent = _router.RenderCurrentState();

            // 启动 Live 显示（简化 API，不使用不存在的扩展方法）
            await AnsiConsole.Live(initialContent)
                .StartAsync(async ctx =>
                {
                    // 在 Live 上下文中运行事件路由器
                    await _router.RunAsync(ctx, cancellationToken);
                    
                    // 最终渲染
                    ctx.UpdateTarget(_router.RenderCurrentState());
                    ctx.Refresh();
                });
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// 单次渲染当前状态（非 Live 模式）
    /// </summary>
    public void RenderOnce()
    {
        var content = _router.RenderCurrentState();
        AnsiConsole.Write(content);
    }

    /// <summary>
    /// 渲染静态消息列表（用于非流式场景）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <remarks>
    /// 当没有活跃流式输出时，使用 Status 模式等待新事件。
    /// </remarks>
    public async Task RenderStaticAsync(CancellationToken cancellationToken)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("yellow"))
            .StartAsync("等待输入...", async ctx =>
            {
                // 等待事件或取消
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken);
                    ctx.Status($"消息数: {_state.Messages.Count}");
                }
            });
    }

    /// <summary>
    /// 创建带状态栏的完整布局
    /// </summary>
    /// <returns>可渲染布局</returns>
    public IRenderable CreateFullLayout()
    {
        // 创建主布局
        var layout = new Layout("Root")
            .SplitColumns(
                new Layout("Main")
                    .SplitRows(
                        new Layout("Messages", _router.RenderCurrentState()),
                        new Layout("Input", CreateInputPanel())));

        // 设置比例
        layout["Main"]["Messages"].Ratio = 4;
        layout["Main"]["Input"].Ratio = 1;

        return layout;
    }

    /// <summary>
    /// 创建输入面板（占位）
    /// </summary>
    private IRenderable CreateInputPanel()
    {
        var isProcessing = _state.IsProcessing;
        var statusText = isProcessing 
            ? "[yellow]处理中...[/]" 
            : "[dim]等待输入[/]";

        return new Panel(statusText)
        {
            Header = new PanelHeader("[bold]输入[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0),
            Expand = true
        };
    }
}