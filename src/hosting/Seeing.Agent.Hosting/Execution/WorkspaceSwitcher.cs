using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>一次工作区切换请求。</summary>
/// <param name="NewRoot">目标工作区根目录。</param>
public sealed record WorkspaceApplyRequest(string NewRoot);

/// <summary>切换结果。失败时调用方不得导航到新工作区。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
/// <param name="ReloadResults">这一轮重载里各处理器的结果（成功时也可能为空）。</param>
public sealed record WorkspaceApplyResult(
    bool Success,
    string? Error,
    IReadOnlyList<ReloadResult> ReloadResults)
{
    /// <summary>构造失败结果。</summary>
    public static WorkspaceApplyResult Failed(string error) =>
        new(false, error, Array.Empty<ReloadResult>());
}

/// <summary>工作区切换的唯一下口。</summary>
public interface IWorkspaceSwitcher
{
    /// <summary>
    /// 首次选定与再次切换共用。返回失败时工作区保持（或回滚到）旧根。
    /// </summary>
    Task<WorkspaceApplyResult> ApplyAsync(WorkspaceApplyRequest request, CancellationToken ct = default);
}

/// <summary>把「重读项目配置」收窄成切换流程需要的一步（便于替身化）。</summary>
public interface IWorkspaceConfigReloader
{
    /// <summary>按当前工作区重新加载配置。</summary>
    Task ReloadAsync(CancellationToken ct = default);
}

/// <summary>跑一轮显式全量重载并返回各处理器结果。</summary>
public interface IWorkspaceReloadInvoker
{
    /// <summary>执行 WorkspaceChange + ConfigChange 两轮，返回合并后的处理器结果。</summary>
    Task<IReadOnlyList<ReloadResult>> ReloadAsync(WorkspaceApplyContext ctx, CancellationToken ct);
}

/// <summary>
/// 唯一的工作区切换实现：首次选定与再次切换共用。
/// <para>
/// 顺序不可调换：先拒绝未决审批，再取消并 <b>等到空闲</b>，然后才改根。
/// 取消信号发出时执行循环还没收尾，此时改根会让收尾写进新工作区的存储。
/// </para>
/// </summary>
public sealed class WorkspaceSwitcher : IWorkspaceSwitcher
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceConfigReloader _configReloader;
    private readonly IWorkspaceHistoryStore _history;
    private readonly IWorkspaceReloadInvoker _reloader;
    private readonly IExecutionInFlightBoundary? _inFlight;
    private readonly IPermissionRequestManager? _permissions;
    private readonly IReadOnlyList<IWorkspaceApplyParticipant> _participants;
    private readonly ILogger<WorkspaceSwitcher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>创建切换器。</summary>
    public WorkspaceSwitcher(
        IWorkspaceProvider workspace,
        IWorkspaceConfigReloader configReloader,
        IWorkspaceHistoryStore history,
        IWorkspaceReloadInvoker reloader,
        IExecutionInFlightBoundary? inFlight,
        IPermissionRequestManager? permissions,
        IEnumerable<IWorkspaceApplyParticipant> participants,
        ILogger<WorkspaceSwitcher> logger)
    {
        _workspace = workspace;
        _configReloader = configReloader;
        _history = history;
        _reloader = reloader;
        _inFlight = inFlight;
        _permissions = permissions;
        _participants = participants.ToArray();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<WorkspaceApplyResult> ApplyAsync(
        WorkspaceApplyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.NewRoot) || !Directory.Exists(request.NewRoot))
            return WorkspaceApplyResult.Failed($"目录不存在: {request.NewRoot}");

        // 同一进程只允许一次切换在途（界面侧也有防重，这里是最终防线）
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return WorkspaceApplyResult.Failed("上一次工作区切换尚未完成");

        try
        {
            var newRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.NewRoot));
            var oldRoot = _workspace.ResolvedRoot;

            if (oldRoot is not null && IsSame(oldRoot, newRoot))
                return new WorkspaceApplyResult(true, null, Array.Empty<ReloadResult>());

            var hasInFlight = _inFlight?.HasInFlight() == true;

            // 1. 先拒绝未决审批：取消执行后，卡在审批上的工具靠这些拒绝解开。
            RejectPendingPermissions();

            // 2. 取消在途，并等到它真的收尾。
            if (hasInFlight && _inFlight is not null)
            {
                var cancelled = await _inFlight.CancelAllInFlightAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("工作区切换：已请求取消 {Count} 个在途执行", cancelled);
                await _inFlight.WaitUntilIdleAsync(ct).ConfigureAwait(false);
            }

            var context = new WorkspaceApplyContext(oldRoot, newRoot);

            // 3. 停掉跑在旧根上的运行时（Gateway / 渠道子进程 / 调度器）。
            foreach (var participant in _participants)
            {
                try
                {
                    await participant.OnApplyingAsync(context, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "参与者 {Component} 在切换前停止失败", participant.ComponentId);
                    return WorkspaceApplyResult.Failed($"{participant.ComponentId} 停止失败: {ex.Message}");
                }
            }

            try
            {
                // 4. 改根 → 重读项目配置 → 一轮显式重载。
                _workspace.SetWorkspaceRoot(newRoot);
                await _configReloader.ReloadAsync(ct).ConfigureAwait(false);

                var results = await _reloader.ReloadAsync(context, ct).ConfigureAwait(false);
                var failed = results.Where(r => !r.Success).ToArray();
                if (failed.Length > 0)
                {
                    var detail = string.Join("; ", failed.Select(f => $"{f.ComponentId}: {f.Error}"));
                    return await RollbackAsync(oldRoot, $"重载失败: {detail}", ct).ConfigureAwait(false);
                }

                // 5. 记录历史（界面里的选择，改 lastInteractive）。
                _history.RecordOpened(newRoot, asDefault: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工作区切换失败: {NewRoot}", newRoot);
                return await RollbackAsync(oldRoot, ex.Message, ct).ConfigureAwait(false);
            }

            // 6. 按新根重新启动。
            foreach (var participant in _participants)
            {
                try
                {
                    await participant.OnAppliedAsync(context, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "参与者 {Component} 在新根启动失败", participant.ComponentId);
                }
            }

            return new WorkspaceApplyResult(true, null, Array.Empty<ReloadResult>());
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RejectPendingPermissions()
    {
        if (_permissions is null)
            return;

        foreach (var pending in _permissions.GetAllPending())
        {
            if (string.IsNullOrEmpty(pending.RequestId))
                continue;

            _permissions.TryResolve(
                pending.RequestId, PermissionEffect.Deny, PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation, "工作区切换", pending.SessionId);
        }
    }

    private async Task<WorkspaceApplyResult> RollbackAsync(string? oldRoot, string error, CancellationToken ct)
    {
        if (oldRoot is null)
            return WorkspaceApplyResult.Failed(error);

        try
        {
            _workspace.SetWorkspaceRoot(oldRoot);
            await _configReloader.ReloadAsync(ct).ConfigureAwait(false);
            await _reloader.ReloadAsync(
                new WorkspaceApplyContext(null, oldRoot), ct).ConfigureAwait(false);
            _logger.LogWarning("工作区切换已回滚到 {OldRoot}: {Error}", oldRoot, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工作区回滚失败，停在错误状态: {OldRoot}", oldRoot);
            return WorkspaceApplyResult.Failed($"{error}；回滚也失败: {ex.Message}");
        }

        return WorkspaceApplyResult.Failed(error);
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);
}
