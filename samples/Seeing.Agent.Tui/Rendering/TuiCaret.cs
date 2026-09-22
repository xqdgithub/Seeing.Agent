using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 编辑插入点在活动区中的位置，用于把终端<b>物理光标</b>移到用户正在输入的位置。
/// <para>
/// 终端把输入法（IME）组合串画在物理光标处：不定光标就会落在活动区末行（状态栏），
/// 表现为「输入法框不在光标位置」。坐标以活动区<b>末行末列</b>为原点：
/// <see cref="RowsBelow"/> 为插入点之下还有几行（输入行剩余行 + 状态栏行），
/// <see cref="Column"/> 为 1 基终端列。
/// </para>
/// </summary>
public readonly record struct TuiCaret(int RowsBelow, int Column);

/// <summary>活动区一帧的产物：渲染体 + 该帧的编辑插入点（无插入点时 <see cref="Caret"/> 为 null）。</summary>
public readonly record struct TuiActiveView(IRenderable View, TuiCaret? Caret);
