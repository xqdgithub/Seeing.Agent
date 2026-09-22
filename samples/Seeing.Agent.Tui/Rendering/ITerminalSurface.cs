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

    /// <summary>在渲染线程上运行交互提示（活动区暂停），保证单写者。</summary>
    Task<T> PromptAsync<T>(Func<IAnsiConsole, Task<T>> prompt, CancellationToken ct = default);

    /// <summary>渲染所用控制台（只读；宽度/高度/能力查询）。</summary>
    IAnsiConsole Console { get; }

    /// <summary>停止渲染线程并恢复终端状态。</summary>
    Task StopAsync();
}
