namespace Seeing.Agent.Abstractions.Ui;

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

/// <summary>
/// UI 贡献注册表：只给事实，不做可见性。
/// <para>
/// 可见性由调用方用 <c>UiVisibility</c> 显式判定：注册表里既有已启用模块的声明，
/// 也有未启用模块的声明（声明早于激活），后者用于「未启用」提示与标题回填。
/// </para>
/// </summary>
public interface IUiContributionRegistry
{
    void AddRange(string ownerId, IReadOnlyList<UiContribution> contributions);
    void RemoveByOwner(string ownerId);
    void ReportInvalidDeclaration(string ownerId, string message);

    IReadOnlyList<UiContributionEntry> All { get; }
    IReadOnlyList<T> Get<T>(string pointId) where T : UiContribution;
    IReadOnlyList<PageContribution> Pages { get; }
    IReadOnlyList<UiContributionDiagnostic> Diagnostics { get; }

    event EventHandler? Changed;
}
