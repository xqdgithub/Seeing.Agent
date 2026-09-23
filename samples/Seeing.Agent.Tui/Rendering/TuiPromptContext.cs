using System.Threading.Channels;
using Seeing.Agent.Tui.Input;
using Spectre.Console;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 自绘模态列表控件的运行上下文（纯数据容器，不含业务逻辑）。
/// <para>由 <see cref="ITerminalSurface.PromptListAsync{T}"/> 在渲染线程构造并交给委托：
/// <see cref="Console"/> 供写，<see cref="Keys"/> 直读 <see cref="TuiKeyInput"/>（含鼠标，绕开 Spectre 按键桥），
/// <see cref="Anchor"/> 供 DSR 底锚命中，<see cref="MouseEnabled"/> 门控鼠标与探针。</para>
/// </summary>
public sealed class TuiPromptContext
{
    public required IAnsiConsole Console { get; init; }

    /// <summary>模态按键流（含 <see cref="TuiInputAction.Mouse"/>）；无通道时为 null（仅降级路径）。</summary>
    public ChannelReader<TuiKeyInput>? Keys { get; init; }

    public ITuiAnchorProbe Anchor { get; init; } = new NullTuiAnchorProbe();

    public bool MouseEnabled { get; init; }

    /// <summary>渲染宽度（终端列）。</summary>
    public int Width { get; init; } = 80;
}
