using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 活动区高度硬上限：只保留渲染结果的最后 <c>maxLines</c> 行。
/// 输入行 + 状态栏恒为最后两项，保尾部即保住它们。
/// </summary>
internal sealed class TailClipRenderable : IRenderable
{
    private readonly IRenderable _inner;
    private readonly int _maxLines;

    public TailClipRenderable(IRenderable inner, int maxLines)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxLines = Math.Max(0, maxLines);
    }

    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        try
        {
            var measurement = _inner.Measure(options, maxWidth);
            return new Measurement(
                Math.Min(measurement.Min, _maxLines),
                Math.Min(measurement.Max, _maxLines));
        }
        catch
        {
            return new Measurement(0, _maxLines);
        }
    }

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        try
        {
            var lines = Segment.SplitLines(_inner.Render(options, maxWidth));
            var start = Math.Max(0, lines.Count - _maxLines);

            var result = new List<Segment>();
            for (var i = start; i < lines.Count; i++)
            {
                if (i > start)
                    result.Add(Segment.LineBreak);

                foreach (var segment in lines[i])
                {
                    if (!segment.IsLineBreak)
                        result.Add(segment);
                }
            }

            return result;
        }
        catch
        {
            return _inner.Render(options, maxWidth);
        }
    }
}
