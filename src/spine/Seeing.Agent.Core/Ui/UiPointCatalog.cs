using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Core.Ui;

/// <summary>扩展点目录。</summary>
public sealed class UiPointCatalog : IUiPointCatalog
{
    private readonly Dictionary<string, UiPoint> _byId;
    private readonly IReadOnlyList<UiPoint> _points;

    public UiPointCatalog(IEnumerable<UiPoint> points)
    {
        _byId = new Dictionary<string, UiPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in points)
        {
            if (!_byId.TryAdd(point.Id, point))
                throw new InvalidOperationException($"重复的扩展点 id：'{point.Id}'。");
        }

        _points = _byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<UiPoint> Points => _points;
    public UiPoint? Find(string pointId)
        => string.IsNullOrWhiteSpace(pointId) ? null : _byId.GetValueOrDefault(pointId);
    public bool Supports(string pointId) => Find(pointId) is not null;
}
