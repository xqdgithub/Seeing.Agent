using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 终端写出口。实现持有专用渲染线程，是唯一写控制台的组件。
/// </summary>
public interface ITerminalSurface
{
    /// <summary>更新活动区（投递到渲染线程，非阻塞）。</summary>
    Task UpdateAsync(IRenderable view, CancellationToken ct = default);

    /// <summary>固化：退出活动区 → 写滚动历史 → 重启活动区。</summary>
    Task CommitAsync(IRenderable committed, CancellationToken ct = default);

    /// <summary>
    /// 在渲染线程上运行交互提示（活动区暂停），保证单写者。
    /// <para>
    /// 委托的第二个参数是<b>提示自身的取消令牌</b>（由出口链接调用方令牌与 Esc 信号而成）：
    /// 提示实现必须把它传给底层提示（如 Spectre <c>ShowAsync(console, token)</c>），
    /// 否则按 Esc 时底层提示会被丢弃在后台继续吞按键（僵尸提示）。
    /// </para>
    /// </summary>
    Task<T> PromptAsync<T>(Func<IAnsiConsole, CancellationToken, Task<T>> prompt, CancellationToken ct = default);

    /// <summary>渲染所用控制台（只读；宽度/高度/能力查询）。</summary>
    IAnsiConsole Console { get; }

    /// <summary>停止渲染线程并恢复终端状态。</summary>
    Task StopAsync();
}
