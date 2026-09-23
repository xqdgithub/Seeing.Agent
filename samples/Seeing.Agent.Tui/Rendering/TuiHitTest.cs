using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 命中几何纯函数：把「DSR 底锚绝对行 + 本帧命中表 + 点击绝对行」换算为候选下标。
/// <para>底锚与命中表必须同帧代次配对（<paramref name="expectedFrameGen"/>）；未校准/跨代次一律返回 null（键盘兜底）。</para>
/// </summary>
public static class TuiHitTest
{
    /// <summary>解析绝对行 <paramref name="absRow"/> 命中的候选下标；无法命中返回 null。</summary>
    public static int? TryResolve(
        ITuiAnchorProbe probe,
        IReadOnlyList<TuiHitRegion> hits,
        long expectedFrameGen,
        int absRow)
    {
        if (probe is null || hits is null || hits.Count == 0)
            return null;

        // 未校准：无底锚可用；跨代次：底锚与命中表非同一帧（M-3），一律失效回落键盘。
        if (!probe.TryGet(out var anchorRow, out var gen) || gen != expectedFrameGen)
            return null;

        foreach (var hit in hits)
        {
            if (anchorRow - hit.RowsFromBottom == absRow)
                return hit.ItemIndex;
        }

        return null;
    }
}
