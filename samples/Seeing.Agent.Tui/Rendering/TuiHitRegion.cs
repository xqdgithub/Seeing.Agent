namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 一个候选项在本帧活动区/模态区中的命中几何：<paramref name="ItemIndex"/> 为候选下标，
/// <paramref name="RowsFromBottom"/> 为该项所在<b>物理行</b>相对「参考末行」的上移行数，
/// <paramref name="FrameGen"/> 为产出本表的渲染帧代次（须与 <c>TuiAnchorProbe</c> 底锚同代次才命中）。
/// </summary>
public readonly record struct TuiHitRegion(int ItemIndex, int RowsFromBottom, long FrameGen);
