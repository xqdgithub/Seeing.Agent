namespace Seeing.Agent.UiEnvelopeProbe;

// ============================================================================
// 这是「UI 扩展信封」契约的可编译验证版本。
// 放在独立命名空间 + 独立项目，是为了与现有 Ui.Contribution 契约并行存在，
// 不干扰主线（旧契约仍服务于当前 30 处构造点）。
// 命名后缀 Probe 仅用于规避与旧类型的同名冲突（SettingsCardContribution / SlotContribution）。
// 验证通过的设计在实施时会去掉后缀并替换旧类型。
// ============================================================================

/// <summary>载荷种类（封闭集）。</summary>
public enum UiPayloadKind
{
    Page,
    SettingsCard,
    Slot
}

/// <summary>扩展点可见性作用域。</summary>
public enum UiVisibilityScope
{
    /// <summary>进程级：只看模块启用集（侧栏 / 设置卡 / 页面）。</summary>
    Process,

    /// <summary>会话级：额外随会话工作模式收窄（会话插槽）。</summary>
    Session
}

/// <summary>UI 扩展点：宿主声明「这里能挂什么」。</summary>
public sealed record UiPoint
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required UiPayloadKind PayloadKind { get; init; }
    public UiVisibilityScope Scope { get; init; } = UiVisibilityScope.Process;
    public int MaxItems { get; init; } = int.MaxValue;
}

/// <summary>宿主支持的扩展点集合。</summary>
public interface IUiPointCatalog
{
    IReadOnlyList<UiPoint> Points { get; }
    UiPoint? Find(string pointId);
    bool Supports(string pointId);
}

/// <summary>
/// UI 贡献信封。
/// <para>
/// 归属由 <see cref="Requires"/> 表达（实测 10 处模块贡献的 Requires 全部等于自身模块 id；
/// 只有壳层自己的页面/设置卡是空数组），因此不另设 ModuleId 字段。
/// </para>
/// </summary>
public abstract record UiContribution
{
    public required string Point { get; init; }
    public required string Id { get; init; }

    /// <summary>归属模块 id 列表；空 = 进程级（不受模块启停影响）。</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    public int Order { get; init; }
}

/// <summary>侧栏导航位。</summary>
public sealed record NavPlacement
{
    public required string Icon { get; init; }
    public string? Group { get; init; }
    public string? GroupIcon { get; init; }
    public int GroupOrder { get; init; }
}

/// <summary>页面贡献（合并旧 NavContribution + RouteContribution）。</summary>
public sealed record PageContributionProbe : UiContribution
{
    /// <summary>路由模板；占位名 <c>{name}</c> 会逐字成为参数键。</summary>
    public required string Route { get; init; }
    public required string Title { get; init; }
    public required string ViewKey { get; init; }

    /// <summary>侧栏位；null = 纯路由不进侧栏。</summary>
    public NavPlacement? Nav { get; init; }
}

/// <summary>设置页卡片贡献。</summary>
public sealed record SettingsCardContributionProbe : UiContribution
{
    public required string Title { get; init; }
    public required string ViewKey { get; init; }
}

/// <summary>命名插槽条目。</summary>
public sealed record SlotContributionProbe : UiContribution
{
    public required string ViewKey { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>注册表条目：把创建方贴在贡献旁边。</summary>
public readonly record struct UiContributionEntry(string OwnerId, UiContribution Contribution);

/// <summary>被拒原因。</summary>
public enum UiDiagnosticKind
{
    UnsupportedPoint,
    PayloadMismatch,
    DuplicateId,
    DuplicateRoute,
    ExceededMaxItems,
    ValidatorRejected,
    InvalidDeclaration,

    /// <summary>跨创建方重复（同一 (扩展点, id) 已被他人声明）。</summary>
    CrossOwnerDuplicate
}

/// <summary>诊断项。</summary>
public sealed record UiContributionDiagnostic(
    UiDiagnosticKind Kind,
    string OwnerId,
    string PointId,
    string ContributionId,
    string Message);

/// <summary>UI 贡献注册表：只给事实，不做可见性。</summary>
public interface IUiContributionRegistry
{
    void AddRange(string ownerId, IReadOnlyList<UiContribution> contributions);
    void RemoveByOwner(string ownerId);
    void ReportInvalidDeclaration(string ownerId, string message);

    IReadOnlyList<UiContributionEntry> All { get; }
    IReadOnlyList<T> Get<T>(string pointId) where T : UiContribution;
    IReadOnlyList<PageContributionProbe> Pages { get; }
    IReadOnlyList<UiContributionDiagnostic> Diagnostics { get; }

    event EventHandler? Changed;
}

/// <summary>视图键目录：只答有没有。</summary>
public interface IUiViewKeyCatalog
{
    bool CanResolve(string viewKey);
    IReadOnlyCollection<string> Keys { get; }
}

/// <summary>宿主侧贡献校验器。</summary>
public interface IUiContributionValidator
{
    string? Validate(UiContribution contribution, UiPoint point);
}

/// <summary>模块侧 UI 贡献端口。</summary>
public interface IUiContributor
{
    IReadOnlyList<UiContribution> DescribeUi();
}

/// <summary>宿主自带 UI 贡献端口（进程级，永不被模块启停门控）。</summary>
public interface IUiHostContributor
{
    /// <summary>必须以 <c>host.</c> 开头。</summary>
    string OwnerId { get; }
    IReadOnlyList<UiContribution> DescribeUi();
}

/// <summary>一次路由匹配结果。</summary>
public sealed record UiRouteMatch(
    PageContributionProbe Page,
    IReadOnlyDictionary<string, object?> Parameters);
