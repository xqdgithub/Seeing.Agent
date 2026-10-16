using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 单活跃会话控制：会话生命周期（新建/切换/继续/CRUD/分支）+ 提交/取消 + 配置切换。
/// <para>取消一律走 <see cref="ISessionRuntime.StopAsync"/>，禁止同步阻塞的取消。</para>
/// </summary>
public sealed class TuiSessionController
{
    private readonly ISessionRuntime _runtime;
    private readonly ISessionDirectory _directory;
    private readonly ISessionManager _sessionManager;
    private readonly ISessionGroupManager _groupManager;

    public TuiSessionController(
        ISessionRuntime runtime,
        ISessionDirectory directory,
        ISessionManager sessionManager,
        ISessionGroupManager groupManager)
    {
        _runtime = runtime;
        _directory = directory;
        _sessionManager = sessionManager;
        _groupManager = groupManager;
    }

    /// <summary>当前活跃会话视图态；无活跃会话时为 null。</summary>
    public TuiViewState? Current { get; private set; }

    /// <summary>活跃会话切换/新建后触发，参数为新活跃会话 Id。</summary>
    public event Func<string, Task>? ActiveSessionChanged;

    public async Task<TuiViewState> NewAsync(string? title, CancellationToken ct = default)
    {
        var session = await _directory.CreateAsync(title, null, null, ct);
        await ActivateAsync(session, ct);
        return Current!;
    }

    public async Task<bool> SwitchAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await _directory.GetAsync(sessionId, ct);
        if (session is null)
            return false;

        await ActivateAsync(session, ct);
        return true;
    }

    public async Task<TuiViewState> ContinueLatestAsync(CancellationToken ct = default)
    {
        var sessions = await _directory.ListSummariesAsync(ct);
        if (sessions.Count == 0)
            return await NewAsync(null, ct);

        var latest = sessions.OrderByDescending(s => s.UpdatedAt).First();
        var session = await _directory.GetAsync(latest.Id, ct)
            ?? throw new InvalidOperationException($"Session not found: {latest.Id}");
        await ActivateAsync(session, ct);
        return Current!;
    }

    public async Task<IReadOnlyList<SessionData>> ListAsync(CancellationToken ct = default)
        => await _directory.ListAsync(ct) ?? [];

    public async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(CancellationToken ct = default)
        => await _directory.ListSummariesAsync(ct) ?? [];

    public async Task RenameAsync(string title, CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        await _directory.RenameAsync(current.SessionId, title, ct);
        current.Title = title;
        current.Touch();
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        await _directory.DeleteAsync(current.SessionId, ct);
        Current = null;
        await ContinueLatestAsync(ct);
    }

    public async Task<TuiViewState> ForkAsync(CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        var branch = await _directory.BranchAsync(current.SessionId, null, ct);
        await ActivateAsync(branch, ct);
        return Current!;
    }

    public async Task<InboxResult> SubmitAsync(ChatInput input, CancellationToken ct = default)
    {
        var current = EnsureCurrent();

        var result = await _runtime.DeliverAsync(new InboxMessage
        {
            TargetSessionId = current.SessionId,
            Sender = new InboxSender { Kind = InboxSenderKind.Human },
            Body = input.Text,
            Delivery = InboxDelivery.Wake,
            Presentation = InboxPresentation.User,
            Attachments = input.Attachments,
            AgentId = NullIfEmpty(current.AgentId),
            ModelId = NullIfEmpty(current.ModelId),
            ModeId = NullIfEmpty(current.AcpMode),
            ThinkingEffort = NullIfEmpty(current.ThinkingEffort)
        }, ct);

        if (result.Success)
        {
            current.IsExecuting = true;
            current.Touch();
        }

        return result;
    }

    public async Task CancelAsync(CancellationToken ct = default)
    {
        var current = Current;
        if (current is null || string.IsNullOrEmpty(current.SessionId))
            return;

        await _runtime.StopAsync(current.SessionId, ct);
    }

    /// <summary>级联取消活跃会话及其子会话下的全部未终态执行（<c>/cancel all</c>）。</summary>
    public async Task<int> CancelAllAsync(CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        return await _runtime.StopAsync(current.SessionId, ct);
    }

    public async Task SetAgentAsync(string agentId, CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        current.AgentId = agentId;
        await _sessionManager.UpdateSessionAsync(current.SessionId, s => s.SelectedAgent = agentId, ct);
        current.Touch();
    }

    public async Task SetModelAsync(string modelId, CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        current.ModelId = modelId;
        await _sessionManager.SetModelAsync(current.SessionId, modelId, ct);
        current.Touch();
    }

    public async Task SetThinkingAsync(string? level, CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        current.ThinkingEffort = level;
        await _sessionManager.SetThinkingEffortAsync(current.SessionId, level, ct);
        current.Touch();
    }

    public async Task SetScenarioAsync(string? scenario, CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        current.Scenario = scenario;
        await _sessionManager.UpdateSessionAsync(current.SessionId, s => s.Scenario = scenario, ct);
        current.Touch();
    }

    public async Task SetAutoApproveAsync(SessionAutoApprove mode, CancellationToken ct = default)
    {
        var current = EnsureCurrent();
        await _sessionManager.SetAutoApproveAsync(current.SessionId, mode, ct);

        // 立即回写视图态：状态栏在下一帧即可反映新模式，无需重载会话。
        current.AutoApprove = mode;
        current.Touch();
    }

    private async Task ActivateAsync(SessionData session, CancellationToken ct)
    {
        await _groupManager.EnsureForSessionAsync(session.Id, ct);

        var state = new TuiViewState { SessionId = session.Id };
        state.ResetFromSession(session);
        Current = state;

        await RaiseActiveSessionChangedAsync(session.Id);
    }

    private async Task RaiseActiveSessionChangedAsync(string sessionId)
    {
        var handlers = ActiveSessionChanged;
        if (handlers is null)
            return;

        foreach (Func<string, Task> handler in handlers.GetInvocationList())
            await handler(sessionId);
    }

    private TuiViewState EnsureCurrent()
        => Current ?? throw new InvalidOperationException("当前无活跃会话");

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
