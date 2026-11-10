using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.UiEnvelopeProbe;

/// <summary>载荷种类 ↔ 类型映射（类型参数不能做 switch case，只能查表）。</summary>
public static class UiPayloadKindMap
{
    private static readonly Dictionary<Type, UiPayloadKind> s_map = new()
    {
        [typeof(PageContributionProbe)] = UiPayloadKind.Page,
        [typeof(SettingsCardContributionProbe)] = UiPayloadKind.SettingsCard,
        [typeof(SlotContributionProbe)] = UiPayloadKind.Slot
    };

    public static bool TryGet(Type contributionType, out UiPayloadKind kind)
    {
        for (var t = contributionType; t is not null && t != typeof(UiContribution); t = t.BaseType)
        {
            if (s_map.TryGetValue(t, out kind))
                return true;
        }

        kind = default;
        return false;
    }
}

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

/// <summary>可见性判定输入。</summary>
public sealed record UiVisibilityContext(
    IModuleCatalog? Catalog,
    IReadOnlySet<string>? ScenarioModuleGate = null)
{
    public static UiVisibilityContext Process(IModuleCatalog? catalog) => new(catalog);
}

public sealed record UiVisibilityDecision(bool Visible, UiVisibilityReason Reason, string? Detail = null);

public enum UiVisibilityReason
{
    Visible,
    CatalogUnavailable,
    ModuleDisabled,
    InvalidRequire,
    ScenarioMismatch
}

/// <summary>可见性唯一实现。</summary>
public static class UiVisibility
{
    public static bool IsVisible(UiContribution contribution, UiPoint point, UiVisibilityContext context)
        => Evaluate(contribution, point, context).Visible;

    public static UiVisibilityDecision Evaluate(
        UiContribution contribution, UiPoint point, UiVisibilityContext context)
    {
        if (context.Catalog is null)
            return new UiVisibilityDecision(false, UiVisibilityReason.CatalogUnavailable);

        foreach (var raw in contribution.Requires)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new UiVisibilityDecision(false, UiVisibilityReason.InvalidRequire, "Requires 含空白项");

            var id = raw.Trim();
            if (!context.Catalog.IsEnabled(id))
                return new UiVisibilityDecision(false, UiVisibilityReason.ModuleDisabled, id);
        }

        if (point.Scope == UiVisibilityScope.Session
            && context.ScenarioModuleGate is { Count: > 0 } gate
            && !IsInGate(contribution, gate))
        {
            return new UiVisibilityDecision(
                false, UiVisibilityReason.ScenarioMismatch, string.Join(",", contribution.Requires));
        }

        return new UiVisibilityDecision(true, UiVisibilityReason.Visible);
    }

    /// <summary>空 Requires（进程级贡献）不受会话门收窄。</summary>
    private static bool IsInGate(UiContribution contribution, IReadOnlySet<string> gate)
    {
        if (contribution.Requires.Count == 0)
            return true;

        foreach (var raw in contribution.Requires)
        {
            if (!string.IsNullOrWhiteSpace(raw) && gate.Contains(raw.Trim()))
                return true;
        }

        return false;
    }
}

/// <summary>注册表实现。</summary>
public sealed class UiContributionRegistry : IUiContributionRegistry
{
    private readonly object _gate = new();
    private readonly IUiPointCatalog? _points;
    private readonly IEnumerable<IUiContributionValidator> _validators;
    private readonly ILogger _logger;

    private readonly Dictionary<string, List<UiContribution>> _byOwner = new(StringComparer.Ordinal);
    private readonly List<UiContributionDiagnostic> _diagnostics = [];

    private IReadOnlyList<UiContributionEntry> _all = [];
    private IReadOnlyList<PageContributionProbe> _pages = [];
    private IReadOnlyList<UiContributionDiagnostic> _diagnosticsSnapshot = [];

    public UiContributionRegistry(
        IUiPointCatalog? points = null,
        IEnumerable<IUiContributionValidator>? validators = null,
        ILogger? logger = null)
    {
        _points = points;
        _validators = validators ?? [];
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsEnabled => _points is not null;

    public IReadOnlyList<UiContributionEntry> All { get { lock (_gate) return _all; } }
    public IReadOnlyList<PageContributionProbe> Pages { get { lock (_gate) return _pages; } }
    public IReadOnlyList<UiContributionDiagnostic> Diagnostics { get { lock (_gate) return _diagnosticsSnapshot; } }

    public event EventHandler? Changed;

    public void AddRange(string ownerId, IReadOnlyList<UiContribution> contributions)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException(null, nameof(ownerId));
        if (_points is null) return;   // 无界面宿主：整体 no-op

        lock (_gate)
        {
            _byOwner.Remove(ownerId);
            _diagnostics.RemoveAll(d => string.Equals(d.OwnerId, ownerId, StringComparison.Ordinal));

            var accepted = new List<UiContribution>(contributions.Count);
            foreach (var contribution in contributions)
            {
                if (contribution is null)
                {
                    Diag(UiDiagnosticKind.InvalidDeclaration, ownerId, "", "", "贡献为 null");
                    continue;
                }

                if (Validate(ownerId, contribution, accepted))
                    accepted.Add(contribution);
            }

            if (accepted.Count > 0)
                _byOwner[ownerId] = accepted;

            Rebuild();
        }

        RaiseChanged();
    }

    public void RemoveByOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return;

        bool removed;
        lock (_gate)
        {
            removed = _byOwner.Remove(ownerId);
            removed |= _diagnostics.RemoveAll(d => string.Equals(d.OwnerId, ownerId, StringComparison.Ordinal)) > 0;
            if (removed) Rebuild();
        }

        if (removed) RaiseChanged();
    }

    public void ReportInvalidDeclaration(string ownerId, string message)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || _points is null) return;

        lock (_gate)
        {
            Diag(UiDiagnosticKind.InvalidDeclaration, ownerId, "", "", message ?? "(无说明)");
            _diagnosticsSnapshot = _diagnostics.ToArray();
        }

        RaiseChanged();
    }

    public IReadOnlyList<T> Get<T>(string pointId) where T : UiContribution
    {
        if (string.IsNullOrWhiteSpace(pointId)) return [];
        if (_points is not null && !_points.Supports(pointId)) return [];

        lock (_gate)
        {
            return _all
                .Where(e => string.Equals(e.Contribution.Point, pointId, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Contribution)
                .OfType<T>()
                .ToArray();
        }
    }

    private bool Validate(string ownerId, UiContribution contribution, List<UiContribution> accepted)
    {
        var point = _points!.Find(contribution.Point);
        if (point is null)
        {
            Diag(UiDiagnosticKind.UnsupportedPoint, ownerId, contribution.Point, contribution.Id,
                $"宿主未声明扩展点 '{contribution.Point}'");
            return false;
        }

        if (!UiPayloadKindMap.TryGet(contribution.GetType(), out var kind))
        {
            Diag(UiDiagnosticKind.PayloadMismatch, ownerId, contribution.Point, contribution.Id,
                $"未登记的载荷类型 '{contribution.GetType().Name}'");
            return false;
        }

        if (kind != point.PayloadKind)
        {
            Diag(UiDiagnosticKind.PayloadMismatch, ownerId, contribution.Point, contribution.Id,
                $"扩展点 '{point.Id}' 只接受 {point.PayloadKind}，收到 {kind}");
            return false;
        }

        if (accepted.Any(c => string.Equals(c.Id, contribution.Id, StringComparison.Ordinal)))
        {
            Diag(UiDiagnosticKind.DuplicateId, ownerId, contribution.Point, contribution.Id, "同批 id 重复");
            return false;
        }

        if (contribution is PageContributionProbe page
            && accepted.OfType<PageContributionProbe>().Any(p =>
                string.Equals(p.Point, page.Point, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Route, page.Route, StringComparison.OrdinalIgnoreCase)))
        {
            Diag(UiDiagnosticKind.DuplicateRoute, ownerId, contribution.Point, contribution.Id,
                $"路由重复：{page.Route}");
            return false;
        }

        var atPoint = accepted.Count(c =>
            string.Equals(c.Point, contribution.Point, StringComparison.OrdinalIgnoreCase));
        if (atPoint >= point.MaxItems)
        {
            Diag(UiDiagnosticKind.ExceededMaxItems, ownerId, contribution.Point, contribution.Id,
                $"扩展点 '{point.Id}' 最多 {point.MaxItems} 条");
            return false;
        }

        foreach (var validator in _validators)
        {
            string? reason;
            try { reason = validator.Validate(contribution, point); }
            catch (Exception ex) { reason = ex.Message; }

            if (reason is not null)
            {
                Diag(UiDiagnosticKind.ValidatorRejected, ownerId, contribution.Point, contribution.Id, reason);
                return false;
            }
        }

        return true;
    }

    private void Rebuild()
    {
        _all = _byOwner
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .SelectMany(p => p.Value.Select(c => new UiContributionEntry(p.Key, c)))
            .OrderBy(e => e.OwnerId, StringComparer.Ordinal)
            .ThenBy(e => e.Contribution.Point, StringComparer.Ordinal)
            .ThenBy(e => e.Contribution.Order)
            .ThenBy(e => e.Contribution.Id, StringComparer.Ordinal)
            .ToArray();

        _pages = _all.Select(e => e.Contribution).OfType<PageContributionProbe>().ToArray();
        _diagnosticsSnapshot = _diagnostics.ToArray();
    }

    private void Diag(UiDiagnosticKind kind, string ownerId, string pointId, string contributionId, string message)
    {
        var index = _diagnostics.FindIndex(d =>
            d.Kind == kind
            && string.Equals(d.OwnerId, ownerId, StringComparison.Ordinal)
            && string.Equals(d.PointId, pointId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.ContributionId, contributionId, StringComparison.Ordinal));

        var diagnostic = new UiContributionDiagnostic(kind, ownerId, pointId, contributionId, message);

        if (index >= 0) _diagnostics[index] = diagnostic;
        else _diagnostics.Add(diagnostic);
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;

        foreach (var target in handler.GetInvocationList().Cast<EventHandler>())
        {
            try { target(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogWarning(ex, "UI 贡献变更订阅者异常"); }
        }
    }
}

/// <summary>路由模板匹配（catch-all 语义与既有测试逐条对齐）。</summary>
public static class UiRouteTemplate
{
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var p = path.Trim();

        var hash = p.IndexOf('#');
        if (hash >= 0) p = p[..hash];

        var query = p.IndexOf('?');
        if (query >= 0) p = p[..query];

        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');

        return p.Length == 0 ? "/" : p;
    }

    public static bool TryMatch(
        string template, string path,
        out IReadOnlyDictionary<string, object?> parameters, out int score)
    {
        parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        score = 0;

        var normalizedTemplate = NormalizePath(template);
        var pathSegments = SplitSegments(NormalizePath(path));
        var templateSegments = SplitSegments(normalizedTemplate);

        var pathIndex = 0;
        var literalCount = 0;
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in templateSegments)
        {
            if (IsCatchAll(segment, out var catchName))
            {
                // 不解码（%2F 原样）；无尾段捕获空串
                bag[catchName] = pathIndex >= pathSegments.Length
                    ? string.Empty
                    : string.Join('/', pathSegments[pathIndex..]);

                parameters = bag;
                score = literalCount * 1000 + normalizedTemplate.Length;
                return true;
            }

            if (pathIndex >= pathSegments.Length)
                return false;

            if (IsParameter(segment, out var paramName))
            {
                bag[paramName] = pathSegments[pathIndex];
                pathIndex++;
                continue;
            }

            if (!string.Equals(segment, pathSegments[pathIndex], StringComparison.OrdinalIgnoreCase))
                return false;

            literalCount++;
            pathIndex++;
        }

        if (pathIndex != pathSegments.Length)
            return false;

        parameters = bag;
        score = literalCount * 1000 + normalizedTemplate.Length;
        return true;
    }

    public static UiRouteMatch? Resolve(string path, IEnumerable<UiContributionEntry> entries)
    {
        var normalized = NormalizePath(path);

        PageContributionProbe? bestPage = null;
        IReadOnlyDictionary<string, object?>? bestParameters = null;
        var bestRoute = string.Empty;
        var bestOwner = string.Empty;
        var bestScore = int.MinValue;

        foreach (var entry in entries)
        {
            if (entry.Contribution is not PageContributionProbe page || string.IsNullOrWhiteSpace(page.Route))
                continue;

            if (!TryMatch(page.Route, normalized, out var parameters, out var score))
                continue;

            var better = score > bestScore;
            if (!better && score == bestScore)
            {
                var routeCmp = string.CompareOrdinal(page.Route, bestRoute);
                better = routeCmp < 0
                    || (routeCmp == 0 && string.CompareOrdinal(entry.OwnerId, bestOwner) < 0);
            }

            if (!better) continue;

            bestPage = page;
            bestParameters = parameters;
            bestRoute = page.Route;
            bestOwner = entry.OwnerId;
            bestScore = score;
        }

        return bestPage is null ? null : new UiRouteMatch(bestPage, bestParameters!);
    }

    private static string[] SplitSegments(string path)
        => path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsParameter(string segment, out string name)
    {
        if (segment.Length >= 3 && segment[0] == '{' && segment[^1] == '}' && segment[1] != '*')
        {
            name = segment[1..^1];
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static bool IsCatchAll(string segment, out string name)
    {
        if (segment.Length >= 4 && segment[0] == '{' && segment[^1] == '}' && segment[1] == '*')
        {
            name = segment[2..^1];
            return true;
        }

        name = string.Empty;
        return false;
    }
}
