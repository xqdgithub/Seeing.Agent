using System.Text;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 活动区光标定位的转义序列（纯函数，便于回归）。
/// </summary>
/// <remarks>
/// 只用 CUU/CUD/CR/CUF 四个最基础的 VT 序列：Spectre 的 <c>Live</c> 本身就在用同一批能力
/// （<c>\r</c> + <c>CursorUp</c> + <c>EraseInLine</c>），故不需要额外探测终端能力。
/// <para>
/// <b>成对使用</b>：<see cref="Place"/> 把光标从活动区末行移到插入点后，下一次 Live 刷新
/// （或提交时的擦除）仍以「光标在末行」为前提，必须先 <see cref="Restore"/> 再让它写终端。
/// </para>
/// </remarks>
internal static class TuiCaretSequences
{
    /// <summary>末行 → 插入点：上移 <c>RowsBelow</c> 行，回到行首，再右移 <c>Column-1</c> 格。</summary>
    public static string Place(TuiCaret caret)
    {
        var builder = new StringBuilder();
        if (caret.RowsBelow > 0)
            builder.Append(FormattableString.Invariant($"\u001b[{caret.RowsBelow}A"));

        // \r 比 CSI G 更省字节；同时清掉「写在最后一格」留下的待换行状态。
        builder.Append('\r');

        // 0 位移必须整段省略：部分终端把 CSI 0 C 当作 1 步。
        if (caret.Column > 1)
            builder.Append(FormattableString.Invariant($"\u001b[{caret.Column - 1}C"));

        return builder.ToString();
    }

    /// <summary>插入点 → 末行：下移 <c>rowsBelow</c> 行（列由接收方自行 <c>\r</c> 归位）。</summary>
    public static string Restore(int rowsBelow)
        => rowsBelow > 0 ? FormattableString.Invariant($"\u001b[{rowsBelow}B") : string.Empty;
}
