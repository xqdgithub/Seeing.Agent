using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 终端写出口。实现持有专用渲染线程，是唯一写控制台的组件。
/// </summary>
public interface ITerminalSurface
{
    /// <summary>
    /// 更新活动区（投递到渲染线程，非阻塞）。
    /// <para>
    /// <paramref name="caret"/> 为编辑插入点：终端把输入法组合串画在<b>物理光标</b>处，
    /// 渲染线程在该帧写完后把光标移到插入点，写下一帧前再放回活动区末行。
    /// </para>
    /// </summary>
    Task UpdateAsync(IRenderable view, TuiCaret? caret = null, CancellationToken ct = default);

    /// <summary>
    /// 更新活动区并随帧携带 <b>DSR 底锚探测代次</b>（鼠标命中校准，spec §6.4）。
    /// <para>
    /// <paramref name="probeGen"/> &gt; 0 且鼠标启用时，渲染线程在本帧写完（<c>UpdateTarget</c> 之后、
    /// 光标移到插入点 <c>PlaceCaret</c> 之前，此刻光标停在活动区末行）写 <c>ESC[6n</c>，
    /// 使底锚绝对行与 <paramref name="probeGen"/> 同代次配对本帧命中表 <c>FrameGen</c>。
    /// </para>
    /// <para><c>probeGen = 0</c> 表示本帧不探测。默认实现转发旧重载（等价不探测），不破坏既有实现方；
    /// 支持鼠标校准的实现方（<c>SpectreTerminalSurface</c>）覆写本重载。</para>
    /// </summary>
    Task UpdateAsync(IRenderable view, TuiCaret? caret, long probeGen, CancellationToken ct = default)
        => UpdateAsync(view, caret, ct);

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

    /// <summary>
    /// 在渲染线程上运行<b>自绘列表提示</b>（活动区暂停，单写者），委托拿到 <see cref="TuiPromptContext"/>：
    /// 既可用 <see cref="IAnsiConsole"/> 写，也可直读 <see cref="TuiKeyInput"/>（含鼠标）做命中/选择。
    /// <para>用于问答/权限候选的自绘控件 <c>TuiListPrompt</c>（区别于走 Spectre 按键桥的 <see cref="PromptAsync{T}"/>）。</para>
    /// <para>委托的 <see cref="CancellationToken"/> 为提示取消令牌（链调用方令牌 + Esc），必须传给底层读取以响应取消。</para>
    /// </summary>
    Task<T> PromptListAsync<T>(Func<TuiPromptContext, CancellationToken, Task<T>> prompt, CancellationToken ct = default);

    /// <summary>渲染所用控制台（只读；宽度/高度/能力查询）。</summary>
    IAnsiConsole Console { get; }

    /// <summary>停止渲染线程并恢复终端状态。</summary>
    Task StopAsync();
}
