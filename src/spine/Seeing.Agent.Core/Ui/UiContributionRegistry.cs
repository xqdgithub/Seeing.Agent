using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Core.Ui;

/// <summary>注册表实现。</summary>
public sealed class UiContributionRegistry : IUiContributionCatalog
{
    private readonly object _gate = new();
    private readonly IUiPointCatalog? _points;
    private readonly IEnumerable<IUiContributionValidator> _validators;
    private readonly ILogger _logger;

    private readonly Dictionary<string, List<UiContribution>> _byOwner = new(StringComparer.Ordinal);
    private readonly List<UiContributionDiagnostic> _diagnostics = [];

    private IReadOnlyList<UiContributionEntry> _all = [];
    private IReadOnlyList<PageContribution> _pages = [];
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
    public IReadOnlyList<PageContribution> Pages { get { lock (_gate) return _pages; } }
    public IReadOnlyList<UiContributionDiagnostic> Diagnostics { get { lock (_gate) return _diagnosticsSnapshot; } }

    public event EventHandler? Changed;

    public void AddRange(string ownerId, IReadOnlyList<UiContribution> contributions)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException(null, nameof(ownerId));
        if (_points is null) return;   // 无界面宿主：整体 no-op

        var isHostOwner = ownerId.StartsWith("host.", StringComparison.Ordinal);

        // 模块不得占用 host.* 命名空间：否则一个叫 "host.memory" 的模块会静默顶掉宿主贡献
        // （_byOwner.Remove 不区分来源）。反向情形（宿主占用模块 id）由 Collector 的
        // 「OwnerId 必须以 host. 开头」校验拦截。
        var namespaceConflict = !isHostOwner
            && _byOwner.Keys.Any(k => string.Equals(k, "host." + ownerId, StringComparison.Ordinal));

        if (namespaceConflict)
        {
            lock (_gate)
            {
                Diag(UiDiagnosticKind.CrossOwnerDuplicate, ownerId, "", "",
                    $"'{ownerId}' 与宿主贡献命名空间 'host.{ownerId}' 冲突，已拒收");
                _diagnosticsSnapshot = _diagnostics.ToArray();
            }

            RaiseChanged();
            return;
        }

        lock (_gate)
        {
            // 幂等：整批替换该创建方（连带清理其旧诊断）。
            // 调用方（Collector）在声明失败时不会调用本方法，而是直接 ReportInvalidDeclaration，
            // 因此这里只需处理「有声明」的路径。
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

        // 模块声明的贡献必须带归属：空 Requires 会让页面在模块停用后依然存活，
        // 但工具/Hook/命令已卸载 —— 表现为「半残但看起来正常」的页面（反模式）。
        // 宿主贡献（host.*）豁免：它们本来就是进程级。
        var isHostOwner = ownerId.StartsWith("host.", StringComparison.Ordinal);
        if (!isHostOwner && contribution.Requires.Count == 0)
        {
            Diag(UiDiagnosticKind.InvalidDeclaration, ownerId, contribution.Point, contribution.Id,
                "模块贡献的 Requires 为空（会变成进程级、模块停用后仍可见）；" +
                $"请显式写 Requires = [{ownerId}]");
            return false;
        }

        if (accepted.Any(c => string.Equals(c.Id, contribution.Id, StringComparison.Ordinal)))
        {
            Diag(UiDiagnosticKind.DuplicateId, ownerId, contribution.Point, contribution.Id, "同批 id 重复");
            return false;
        }

        // 跨 owner：同一 (扩展点, id) 全局唯一。否则 Settings.razor 的 @key 会重复 → 渲染崩。
        // 注意用 _byOwner 实时查（_all 是上一次 Rebuild 的快照，在本次 AddRange 内尚未更新）。
        var crossOwner = _byOwner
            .Where(p => !string.Equals(p.Key, ownerId, StringComparison.Ordinal))
            .SelectMany(p => p.Value)
            .FirstOrDefault(c =>
                string.Equals(c.Point, contribution.Point, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Id, contribution.Id, StringComparison.Ordinal));

        if (crossOwner is not null)
        {
            Diag(UiDiagnosticKind.CrossOwnerDuplicate, ownerId, contribution.Point, contribution.Id,
                "同一 (扩展点, id) 已被其他创建方声明");
            return false;
        }

        if (contribution is PageContribution page
            && accepted.OfType<PageContribution>().Any(p =>
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

        _pages = _all.Select(e => e.Contribution).OfType<PageContribution>().ToArray();
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
