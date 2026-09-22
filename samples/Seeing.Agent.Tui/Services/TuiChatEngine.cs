using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Llm;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Rendering.Prompts;
using Seeing.Session.Core;
using Seeing.Session.Persistence;
using Seeing.Session.Storage;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// TUI 主引擎（单活跃会话、单线程决策）。生产者：输入线程与事件泵分别经通道投递，
/// 主循环消费后唯一经 <see cref="ITerminalSurface"/> 写入终端；权限/问答提示串行化。
/// </summary>
public sealed class TuiChatEngine
{
    private const int RedrawIntervalMs = 60;
    private const int RedrawCharsNormal = 8;
    private const int RedrawCharsCode = 20;
    private const int CommitLines = 24;
    /// <summary>执行中「Esc 二次确认取消」的确认窗口（超时视为放弃，需重新按两次）。</summary>
    private const int CancelConfirmWindowMs = 3000;
    /// <summary>选择器 Esc 取消哨兵（与真实候选值不可能冲突）。</summary>
    internal const string CompletionCancelSentinel = "\u0000__tui_completion_cancel__";

    // 事件泵重建退避：失败次数越多退避越久（指数，封顶），避免「每次输入即重建、重建即失败」的高频重试。
    private const int RebuildBackoffBaseMs = 250;
    private const int RebuildBackoffMaxMs = 5000;
    private const int MaxRebuildBackoffShift = 5;

    private readonly TuiSessionController _sessions;
    private readonly TuiCommandRouter _commands;
    private readonly TuiPermissionQueue _permissions;
    private readonly TuiQuestionQueue _questions;
    private readonly TuiSurfaceProvider _surfaceProvider;
    private readonly ITerminalSurface _surface;
    private readonly TuiPromptInputRelay _inputRelay;
    private readonly IPermissionSurfaceRegistry _permissionRegistry;
    private readonly IQuestionSurfaceRegistry _questionRegistry;
    private readonly ISessionGroupManager _groups;
    private readonly IExecutionSubmitter _submitter;
    private readonly ISessionStore _sessionStore;
    private readonly ISessionGroupStore _groupStore;
    private readonly Func<string, ITuiEventPump> _pumpFactory;
    private readonly TuiAttachmentResolver _attachmentResolver;
    private readonly TuiCompletionProvider _completion;
    private readonly IAgentRegistry _agents;
    private readonly IModelConfigManager _models;
    private readonly ILlmService _llm;
    private readonly IScenarioCatalog _scenarios;
    private readonly IExecutionStatusProvider _executionStatus;

    // 状态栏数据源：工作目录取项目根（缺失时回落进程 cwd）；上下文用量取 TokenBudget 快照；
    // 全局审批开关取热重载配置。
    private readonly IWorkspaceProvider? _workspace;
    private readonly Seeing.Agent.TokenBudget.IBudgetStatusNotifier? _budgetNotifier;
    private readonly IOptionsMonitor<SeeingAgentOptions>? _seeingOptions;

    private readonly IHostApplicationLifetime? _hostLifetime;
    private readonly ILogger<TuiChatEngine>? _logger;

    private readonly TuiInputEditorState _editor = new();
    private readonly TuiCommitLedger _ledger = new();
    private readonly HashSet<string> _dismissedPermissionRequests = new(StringComparer.Ordinal);
    private readonly List<PendingAttachment> _pendingAttachments = [];

    // 渲染选项与渲染器可热替换：/reasoning 切换时按新选项重建渲染器。
    private TuiRenderOptions _renderOptions;
    private TuiRenderer _renderer;

    private SessionContext? _context;
    private TuiInputThread? _inputThread;
    private bool _permissionRegistered;
    private bool _questionRegistered;
    private long _lastRenderedChars;
    private DateTime _lastRenderAt = DateTime.MinValue;

    // 上一次真正渲染时的 tick 判据快照：周期 tick 的脏检查依据（详见 ShouldRenderOnTick）。
    private TickRenderSnapshot _lastRenderedTick;

    // 活动区外写入（固化提交）代次：提交会拆掉活动区，故代次变化必须触发重绘。
    private int _terminalCommits;

    // 执行中「Esc 二次确认取消」的武装时间（null=未武装）。
    private DateTime? _cancelArmedAt;

    // 事件泵「已完成」后的重建退避状态（提交前按需重建；失败累积，成功清零）。
    private int _rebuildFailureCount;
    private DateTime _lastRebuildFailureAt = DateTime.MinValue;

    public TuiChatEngine(
        TuiSessionController sessions,
        TuiCommandRouter commands,
        TuiPermissionQueue permissions,
        TuiQuestionQueue questions,
        TuiSurfaceProvider surfaceProvider,
        ITerminalSurface surface,
        TuiPromptInputRelay inputRelay,
        TuiRenderer renderer,
        TuiRenderOptions renderOptions,
        IPermissionSurfaceRegistry permissionRegistry,
        IQuestionSurfaceRegistry questionRegistry,
        ISessionGroupManager groups,
        IExecutionSubmitter submitter,
        ISessionStore sessionStore,
        ISessionGroupStore groupStore,
        Func<string, ITuiEventPump> pumpFactory,
        TuiAttachmentResolver attachmentResolver,
        TuiCompletionProvider completion,
        IAgentRegistry agents,
        IModelConfigManager models,
        ILlmService llm,
        IScenarioCatalog scenarios,
        IExecutionStatusProvider executionStatus,
        IHostApplicationLifetime? hostLifetime = null,
        ILogger<TuiChatEngine>? logger = null,
        IWorkspaceProvider? workspace = null,
        Seeing.Agent.TokenBudget.IBudgetStatusNotifier? budgetNotifier = null,
        IOptionsMonitor<SeeingAgentOptions>? seeingOptions = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
        _surfaceProvider = surfaceProvider ?? throw new ArgumentNullException(nameof(surfaceProvider));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _inputRelay = inputRelay ?? throw new ArgumentNullException(nameof(inputRelay));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _renderOptions = renderOptions ?? throw new ArgumentNullException(nameof(renderOptions));
        _permissionRegistry = permissionRegistry ?? throw new ArgumentNullException(nameof(permissionRegistry));
        _questionRegistry = questionRegistry ?? throw new ArgumentNullException(nameof(questionRegistry));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _groupStore = groupStore ?? throw new ArgumentNullException(nameof(groupStore));
        _pumpFactory = pumpFactory ?? throw new ArgumentNullException(nameof(pumpFactory));
        _attachmentResolver = attachmentResolver ?? throw new ArgumentNullException(nameof(attachmentResolver));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));
        _executionStatus = executionStatus ?? throw new ArgumentNullException(nameof(executionStatus));
        _workspace = workspace;
        _budgetNotifier = budgetNotifier;
        _seeingOptions = seeingOptions;
        _hostLifetime = hostLifetime;
        _logger = logger;
    }

    /// <summary>运行主循环；非交互终端返回 2，会话恢复失败返回 3，异常返回 1，正常退出返回 0。</summary>
    public async Task<int> RunAsync(TuiCliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            await Console.Error.WriteLineAsync("seeing-tui 需要交互式终端（stdin/stdout 非 TTY），已退出。");
            return 2;
        }

        var exitCode = 0;
        try
        {
            var initCode = await InitializeSessionAsync(options).ConfigureAwait(false);
            if (initCode != 0)
                return initCode;

            exitCode = await RunLoopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TUI 引擎异常退出");
            exitCode = 1;
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }

        return exitCode;
    }

    private async Task<int> InitializeSessionAsync(TuiCliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Resume))
        {
            if (!await _sessions.SwitchAsync(options.Resume!.Trim()).ConfigureAwait(false))
            {
                _logger?.LogWarning("恢复会话失败: {SessionId}", options.Resume);
                return 3;
            }
        }
        else if (options.Continue)
        {
            await _sessions.ContinueLatestAsync().ConfigureAwait(false);
        }
        else
        {
            await _sessions.NewAsync(null).ConfigureAwait(false);
        }

        var state = _sessions.Current ?? throw new InvalidOperationException("无法创建活跃会话");

        if (!string.IsNullOrWhiteSpace(options.Agent))
            await _sessions.SetAgentAsync(options.Agent!.Trim()).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(options.Model))
            await _sessions.SetModelAsync(options.Model!.Trim()).ConfigureAwait(false);

        _surfaceProvider.Initialize();
        await _surfaceProvider.SetActiveSessionAsync(state.SessionId).ConfigureAwait(false);

        _permissionRegistry.Register(_surfaceProvider);
        _permissionRegistered = true;
        _questionRegistry.Register(_surfaceProvider);
        _questionRegistered = true;

        _logger?.LogInformation("TUI 启动：会话 {SessionId}", state.SessionId);
        return 0;
    }

    private async Task<int> RunLoopAsync()
    {
        using var cts = new CancellationTokenSource();
        // 宿主停止（SIGTERM/SIGINT 经 Host 生命周期）→ 取消主循环，解除读输入的阻塞等待。
        // using 确保循环退出即释放注册，避免其残留到下次运行或悬挂闭包。
        using var stopRegistration = RegisterStopCancellation(_hostLifetime, cts);

        _inputThread = new TuiInputThread(new RawInputReader());
        await _inputThread.StartAsync(cts.Token).ConfigureAwait(false);
        _inputRelay.Attach(_inputThread.Reader, cts.Token);

        await BindSessionAsync(cts.Token).ConfigureAwait(false);

        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(RedrawIntervalMs));
        var tickTask = ticker.WaitForNextTickAsync(cts.Token).AsTask();
        var inputTask = ReadKeyAsync(_inputRelay.EngineReader, cts.Token);
        Task<IMessageEvent?>? eventTask = ReadEventAsync(_context!.Pump.Reader, cts.Token);

        var exit = false;
        // 本轮唤醒是否只来自周期 tick：tick 的重绘要过脏检查（空闲时不写终端，避免擦掉输入法组合串）。
        var wokeByTick = false;
        while (!exit && !cts.IsCancellationRequested)
        {
            await PromptPendingAsync(cts.Token).ConfigureAwait(false);
            await MaybeRenderAsync(false, cts.Token, tickOnly: wokeByTick).ConfigureAwait(false);

            Task done;
            if (inputTask.IsCompleted)
                done = inputTask;
            else if (eventTask is { IsCompleted: true })
                done = eventTask;
            else
                done = await Task.WhenAny(BuildWaitSet(inputTask, eventTask, tickTask)).ConfigureAwait(false);

            if (done == inputTask)
            {
                wokeByTick = false;
                var key = await inputTask.ConfigureAwait(false);
                inputTask = ReadKeyAsync(_inputRelay.EngineReader, cts.Token);
                if (key is null)
                    break;

                exit = await HandleInputAsync(key.Value, cts.Token).ConfigureAwait(false);
                if (await TryRebindAsync(cts.Token).ConfigureAwait(false))
                {
                    eventTask = ReadEventAsync(_context!.Pump.Reader, cts.Token);
                }
                else if (eventTask is null && _context is not null && !_context.Pump.Reader.Completion.IsCompleted)
                {
                    // 提交路径（SubmitToServerAsync 之前）可能已按需重建事件泵：恢复正常读取，解除无事件模式。
                    eventTask = ReadEventAsync(_context.Pump.Reader, cts.Token);
                }
            }
            else if (eventTask is not null && done == eventTask)
            {
                wokeByTick = false;
                var evt = await eventTask.ConfigureAwait(false);
                if (ShouldEnterNoEventMode(evt))
                {
                    // 事件源已结束（泵通道完成）：不再重建读取任务，否则每轮立即命中已完成任务形成忙循环。
                    eventTask = null;
                }
                else
                {
                    eventTask = ReadEventAsync(_context!.Pump.Reader, cts.Token);
                    await HandleEventAsync(evt!, cts.Token).ConfigureAwait(false);
                }
            }
            else if (done == tickTask)
            {
                wokeByTick = true;
                tickTask = ticker.WaitForNextTickAsync(cts.Token).AsTask();
            }
        }

        await cts.CancelAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 把宿主停止信号（<see cref="IHostApplicationLifetime.ApplicationStopping"/>，由 SIGTERM/SIGINT 触发）
    /// 接到引擎取消令牌：停止即取消主循环，解除读输入阻塞，使 <c>Program.cs</c> 的 <c>host.StopAsync()</c> 能返回。
    /// <para>返回的注册必须在循环退出后释放（调用方 using）；无宿主生命周期时返回 <c>default</c>（无操作）。</para>
    /// </summary>
    internal static CancellationTokenRegistration RegisterStopCancellation(
        IHostApplicationLifetime? hostLifetime,
        CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
        return hostLifetime?.ApplicationStopping.Register(cts.Cancel) ?? default;
    }

    private async Task BindSessionAsync(CancellationToken ct)
    {
        var state = _sessions.Current ?? throw new InvalidOperationException("当前无活跃会话");
        var previous = _context;
        var context = CreateContext(state);
        await context.Pump.StartAsync(ct).ConfigureAwait(false);
        _context = context;

        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);

        _ledger.Clear();
        _lastRenderedChars = 0;
        _lastRenderAt = DateTime.MinValue;
        // 换泵/换上下文：清空事件源重建退避状态。
        _rebuildFailureCount = 0;
        _lastRebuildFailureAt = DateTime.MinValue;

        await context.Tracker.ReconcileAsync().ConfigureAwait(false);
        await RenderAsync(ct).ConfigureAwait(false);
        // 历史块均为终态，唯一提交入口是事件到达 → 恢复会话时需主动固化一次，否则屏幕只剩输入行与状态栏。
        await CommitTerminalBlocksAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> TryRebindAsync(CancellationToken ct)
    {
        var current = _sessions.Current;
        if (current is null || _context is null)
            return false;
        // 以状态对象身份判定：控制器每次 Activate 都新建 TuiViewState，仅比较 SessionId 会漏掉「同会话重开」。
        if (ReferenceEquals(_context.State, current))
            return false;

        await _surfaceProvider.SetActiveSessionAsync(current.SessionId, ct).ConfigureAwait(false);

        var previous = _context;
        var context = CreateContext(current);
        await context.Pump.StartAsync(ct).ConfigureAwait(false);
        _context = context;

        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);

        _ledger.Clear();
        _lastRenderedChars = 0;
        _lastRenderAt = DateTime.MinValue;
        // 换泵/换上下文：清空事件源重建退避状态。
        _rebuildFailureCount = 0;
        _lastRebuildFailureAt = DateTime.MinValue;

        await context.Tracker.ReconcileAsync().ConfigureAwait(false);
        await RenderAsync(ct).ConfigureAwait(false);
        // 历史块均为终态，唯一提交入口是事件到达 → 重绑到已有会话后同样需主动固化一次。
        await CommitTerminalBlocksAsync(ct).ConfigureAwait(false);
        return true;
    }

    private SessionContext CreateContext(TuiViewState state)
        => new(
            state,
            new TuiEventInterpreter(state),
            new TuiTaskTracker(state, _pumpFactory, _groups),
            _pumpFactory(state.SessionId));

    private async Task<bool> HandleInputAsync(TuiKeyInput key, CancellationToken ct)
    {
        // Cancel 会清空输入，需在 Apply 前记录「原本是否为空」以判定空闲 Ctrl+C。
        var editorWasEmpty = _editor.Text.Length == 0;
        _editor.Apply(key);

        switch (key.Action)
        {
            case TuiInputAction.None:
                return false;

            case TuiInputAction.Submit:
            {
                var text = _editor.Text;
                _editor.Clear();
                var exit = await SubmitAsync(text, ct).ConfigureAwait(false);
                _lastRenderAt = DateTime.MinValue;
                return exit;
            }

            case TuiInputAction.Complete:
                await HandleCompletionAsync(ct).ConfigureAwait(false);
                _lastRenderAt = DateTime.MinValue;
                return false;

            case TuiInputAction.ExitRequested:
                return true;

            case TuiInputAction.Cancel:
                // 优先级（spec §12.1）：输入非空先清空（Apply 已清空）；输入为空且执行中才取消执行；空闲则二次确认退出。
                if (!editorWasEmpty)
                {
                    _cancelArmedAt = null;
                    return false;
                }

                if (_context?.State.IsExecuting == true)
                {
                    // 执行中取消：Esc 需二次确认（防误触）；Ctrl+C 视为明确意图立即取消。
                    if (key.IsEscape && !IsCancelArmed(_cancelArmedAt, DateTime.UtcNow, executing: true))
                    {
                        _cancelArmedAt = DateTime.UtcNow;
                        _lastRenderAt = DateTime.MinValue;   // 立即重绘以显示「再按一次」提示
                        return false;
                    }

                    _cancelArmedAt = null;
                    await _sessions.CancelAsync(ct).ConfigureAwait(false);
                    return false;
                }

                _cancelArmedAt = null;
                return await ConfirmExitAsync(ct).ConfigureAwait(false);

            default:
                _cancelArmedAt = null;
                _lastRenderAt = DateTime.MinValue;
                return false;
        }
    }

    private async Task<bool> SubmitAsync(string text, CancellationToken ct)
    {
        // 本地附件/会话命令先于路由器拦截。
        if (await TryHandleLocalInteractiveAsync(text, ct).ConfigureAwait(false))
            return false;

        var remaining = text;
        if (_attachmentResolver.TryParseInline(text, out var parsed, out var paths))
        {
            foreach (var path in paths)
                await StageAttachmentAsync(path, ct).ConfigureAwait(false);

            remaining = parsed;
        }

        if (_commands.IsLocal(remaining))
        {
            TuiCommandResult result;
            // 本地命令（/agent、/model、/scenario、/thinking、/sessions、/resume 无参等）会经
            // ctx.Surface.PromptAsync 弹选择器，必须包裹提示输入模式；否则按键被路由到引擎通道，
            // 提示永远读不到键 → 主循环永久 await（Ctrl+C 也失效）。relay 为嵌套计数，包裹式用法安全。
            _inputRelay.BeginPrompt();
            try
            {
                result = await _commands.ExecuteAsync(remaining, BuildCommandContext(), ct).ConfigureAwait(false);
            }
            finally
            {
                _inputRelay.EndPrompt();
            }

            if (result.ToggleReasoning)
                ApplyReasoningOption();

            if (result.Output is not null)
            {
                await CommitBestEffortAsync(result.Output, "本地命令输出", ct).ConfigureAwait(false);
                _lastRenderedChars = 0;
            }

            if (result.ForwardText is not null)
                await SubmitToServerAsync(ChatInput.FromText(result.ForwardText), ct).ConfigureAwait(false);

            return result.ExitRequested;
        }

        // 放宽纯附件提交：剩余文本为空但有待发附件时同样提交。
        if (string.IsNullOrWhiteSpace(remaining) && _pendingAttachments.Count == 0)
            return false;

        // 本地立即回显用户消息：服务端历史只在会话激活时加载，实时回合不会渲染用户侧。
        await CommitUserMessageAsync(remaining, ct).ConfigureAwait(false);

        if (!await SubmitToServerAsync(BuildChatInput(remaining), ct).ConfigureAwait(false))
            return false;

        _pendingAttachments.Clear();
        _editor.ClearAttachments();
        return false;
    }

    /// <summary>把用户消息一次性写入滚动历史（不入 ViewState，避免与服务端历史键重复）。</summary>
    private async Task CommitUserMessageAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var block = BuildUserEchoBlock(text);

        try
        {
            NoteTerminalCommitted();
            await _surface.CommitAsync(_renderer.BuildCommitted(block, GetWidth()), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "回显用户消息失败");
        }
    }

    /// <summary>用户消息回显块（终态、不入 ViewState）。</summary>
    internal static TuiBlock BuildUserEchoBlock(string text) => new()
    {
        Key = "user:local",
        Kind = TuiBlockKind.User,
        Text = text,
        IsTerminal = true,
    };

    private TuiCommandContext BuildCommandContext()
        => new(
            _sessions,
            _permissions,
            _questions,
            _surface,
            _context?.State,
            _agents,
            _models,
            _llm,
            _scenarios);

    /// <summary>按路由器的推理开关重建渲染选项与渲染器。</summary>
    private void ApplyReasoningOption()
    {
        _renderOptions = _renderOptions with { ShowReasoning = _commands.ReasoningEnabled };
        _renderer = new TuiRenderer(_renderOptions);
    }

    /// <summary>待发附件 + 文本 → <see cref="ChatInput"/>（纯附件时文本为 null）。</summary>
    private ChatInput BuildChatInput(string text)
    {
        var attachments = ToChatAttachments(_pendingAttachments.Select(p => p.Attachment));
        return new ChatInput
        {
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            Attachments = attachments.Count > 0 ? attachments : null,
        };
    }

    /// <summary>将附件记录映射为协议附件（纯函数，供接线测试）。</summary>
    public static List<ChatAttachment> ToChatAttachments(IEnumerable<TuiAttachment> attachments)
        => attachments
            .Select(a => new ChatAttachment
            {
                FileName = a.FileName,
                MimeType = a.MimeType,
                Base64Data = a.Base64Data,
            })
            .ToList();

    private async Task<bool> SubmitToServerAsync(ChatInput input, CancellationToken ct)
    {
        // 提交前按需重建已完成的事件泵：否则同会话再次执行的事件无订阅者、永不渲染。
        await EnsureEventSourceRebuiltAsync(ct).ConfigureAwait(false);

        var result = await _sessions.SubmitAsync(input, ct).ConfigureAwait(false);
        if (result.Success)
            return true;

        _logger?.LogWarning("提交执行失败: {Error}", result.Error);
        if (_context is not null)
            _context.State.LastError = result.Error;

        // 用户消息已回显，若不在滚动历史明确告知失败，用户会误以为已发送。
        await CommitNoticeAsync($"提交失败：{result.Error ?? "未知错误"}", ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// 事件泵已完成（如宿主会话空闲回收触发 <c>CompleteSession</c>）时按需重建，使同一会话再次提交后事件恢复渲染。
    /// <para>失败不抛出：记录日志、保持无事件模式，并按指数退避节流，避免高频重试与 100% CPU。</para>
    /// <para>账本处理：重建发生在「下一次提交之前」，此时宿主事件环形缓冲已被 <c>CompleteSession</c> 清空，
    /// 新订阅不会回放历史事件；而视图态中旧的终态块仍被账本标记为已固化，若此处清空账本，
    /// <see cref="CommitTerminalBlocksAsync"/> 会把历史块再固化一次导致滚动历史重复。故保留账本。</para>
    /// </summary>
    private async Task<bool> EnsureEventSourceRebuiltAsync(CancellationToken ct)
    {
        var context = _context;
        if (context is null || !RequiresEventSourceRebuild(context.Pump.Reader.Completion.IsCompleted))
            return false;

        if (!ShouldAttemptEventSourceRebuild(_lastRebuildFailureAt, DateTime.UtcNow, _rebuildFailureCount))
            return false;

        ITuiEventPump? pump = null;
        try
        {
            pump = _pumpFactory(context.SessionId);
            await pump.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = UpdateRebuildBackoff(_rebuildFailureCount, _lastRebuildFailureAt, success: false, DateTime.UtcNow);
            _rebuildFailureCount = failed.FailureCount;
            _lastRebuildFailureAt = failed.LastFailureAt;
            _logger?.LogWarning(ex, "重建事件泵失败，保持无事件模式: {SessionId}", context.SessionId);

            if (pump is not null)
                await DisposePumpQuietlyAsync(pump).ConfigureAwait(false);
            return false;
        }

        var updated = UpdateRebuildBackoff(_rebuildFailureCount, _lastRebuildFailureAt, success: true, DateTime.UtcNow);
        _rebuildFailureCount = updated.FailureCount;
        _lastRebuildFailureAt = updated.LastFailureAt;

        var previous = context.ReplacePump(pump!);
        await DisposePumpQuietlyAsync(previous).ConfigureAwait(false);
        _logger?.LogInformation("事件泵已重建: {SessionId}", context.SessionId);
        return true;
    }

    private async Task DisposePumpQuietlyAsync(ITuiEventPump? pump)
    {
        if (pump is null)
            return;

        try
        {
            await pump.StopAsync().ConfigureAwait(false);
            await pump.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "释放旧事件泵失败");
        }
    }

    /// <summary>引擎侧本地命令拦截（路由器未收录的交互）：/attach、/detach、/rename（无参）、/delete。</summary>
    private async Task<bool> TryHandleLocalInteractiveAsync(string text, CancellationToken ct)
    {
        if (!TryParseLocal(text, out var name, out var args))
            return false;

        switch (name)
        {
            case "attach":
                await HandleAttachAsync(args, ct).ConfigureAwait(false);
                return true;

            case "detach":
                await HandleDetachAsync(args, ct).ConfigureAwait(false);
                return true;

            case "rename" when string.IsNullOrWhiteSpace(args):
                await RenameInteractiveAsync(ct).ConfigureAwait(false);
                return true;

            case "delete":
                await DeleteInteractiveAsync(ct).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseLocal(string input, out string name, out string args)
    {
        name = string.Empty;
        args = string.Empty;

        var trimmed = input.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/')
            return false;

        var space = trimmed.IndexOf(' ');
        name = (space < 0 ? trimmed[1..] : trimmed[1..space]).ToLowerInvariant();
        args = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        return name.Length > 0;
    }

    private async Task HandleAttachAsync(string args, CancellationToken ct)
    {
        var paths = SplitAttachmentArgs(args);
        if (paths.Count == 0)
        {
            await CommitNoticeAsync("用法：/attach <path...>", ct).ConfigureAwait(false);
            return;
        }

        foreach (var path in paths)
            await StageAttachmentAsync(path, ct).ConfigureAwait(false);
    }

    private async Task StageAttachmentAsync(string path, CancellationToken ct)
    {
        try
        {
            var attachment = await _attachmentResolver.LoadAsync(path, ct).ConfigureAwait(false);
            var display = $"{attachment.FileName} ({TuiAttachmentResolver.FormatSize(attachment.Size)})";
            _pendingAttachments.Add(new PendingAttachment(attachment, display));
            _editor.AddAttachment(display);
            await CommitNoticeAsync($"已附加：{display}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取附件失败: {Path}", path);
            await CommitNoticeAsync($"附件读取失败：{path}（{ex.Message}）", ct).ConfigureAwait(false);
        }
    }

    private async Task HandleDetachAsync(string args, CancellationToken ct)
    {
        if (_pendingAttachments.Count == 0)
        {
            await CommitNoticeAsync("(无附件)", ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(args, "all", StringComparison.OrdinalIgnoreCase))
        {
            _pendingAttachments.Clear();
            _editor.ClearAttachments();
            await CommitNoticeAsync("已移除全部附件", ct).ConfigureAwait(false);
            return;
        }

        var index = _pendingAttachments.Count - 1;
        if (!string.IsNullOrWhiteSpace(args))
        {
            if (!int.TryParse(args, out var number) || number < 1 || number > _pendingAttachments.Count)
            {
                await CommitNoticeAsync("用法：/detach [n|all]（n 从 1 起）", ct).ConfigureAwait(false);
                return;
            }

            index = number - 1;
        }

        var removed = _pendingAttachments[index].Display;
        _pendingAttachments.RemoveAt(index);
        _editor.RemoveAttachment(index);
        await CommitNoticeAsync($"已移除附件：{removed}", ct).ConfigureAwait(false);
    }

    /// <summary>拆分 /attach 参数：空白分隔，支持 "含空格路径" 引号包裹。</summary>
    private static List<string> SplitAttachmentArgs(string args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args))
            return result;

        var index = 0;
        while (index < args.Length)
        {
            while (index < args.Length && char.IsWhiteSpace(args[index]))
                index++;

            if (index >= args.Length)
                break;

            string token;
            if (args[index] == '"')
            {
                var close = args.IndexOf('"', index + 1);
                if (close < 0)
                {
                    token = args[(index + 1)..];
                    index = args.Length;
                }
                else
                {
                    token = args[(index + 1)..close];
                    index = close + 1;
                }
            }
            else
            {
                var start = index;
                while (index < args.Length && !char.IsWhiteSpace(args[index]))
                    index++;
                token = args[start..index];
            }

            if (token.Length > 0)
                result.Add(token);
        }

        return result;
    }

    private async Task RenameInteractiveAsync(CancellationToken ct)
    {
        var title = await PromptTextAsync("新标题：", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title))
        {
            await CommitNoticeAsync("(未重命名)", ct).ConfigureAwait(false);
            return;
        }

        var trimmed = title.Trim();
        await _sessions.RenameAsync(trimmed, ct).ConfigureAwait(false);
        await CommitNoticeAsync($"已重命名为 {trimmed}", ct).ConfigureAwait(false);
    }

    private async Task DeleteInteractiveAsync(CancellationToken ct)
    {
        var confirmed = await PromptConfirmAsync("确认删除当前会话？", ct).ConfigureAwait(false);
        if (!confirmed)
        {
            await CommitNoticeAsync("(已取消删除)", ct).ConfigureAwait(false);
            return;
        }

        await _sessions.DeleteAsync(ct).ConfigureAwait(false);
        await CommitNoticeAsync("已删除活跃会话", ct).ConfigureAwait(false);
    }

    private async Task HandleCompletionAsync(CancellationToken ct)
    {
        var apply = _completion.TryApply(_editor.Text, _editor.Cursor);
        if (apply is not null)
        {
            _editor.SetTextAndCursor(apply.Value.Text, apply.Value.Cursor);
            return;
        }

        var items = _completion.GetCompletions(_editor.Text, _editor.Cursor);
        if (items.Count == 0)
            return;

        var chosen = await PromptChoiceAsync("补全候选", items, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(chosen))
            return;

        _editor.SetTextAndCursor(chosen + " ", chosen.Length + 1);
    }

    private async Task<bool> ConfirmExitAsync(CancellationToken ct)
        => await PromptConfirmAsync("退出 seeing-tui？", ct).ConfigureAwait(false);

    private async Task CommitNoticeAsync(string message, CancellationToken ct)
    {
        await CommitBestEffortAsync(new Text(message), "提示", ct).ConfigureAwait(false);
        _lastRenderedChars = 0;
    }

    /// <summary>
    /// 尽力而为提交：<see cref="ITerminalSurface.CommitAsync"/> 在投递失败/渲染线程失效时抛出可辨识异常，
    /// 此处吞掉并记录日志，绝不允许异常冒泡杀死主循环（非关键路径不重试；关键路径见 CommitBlockSafeAsync）。
    /// </summary>
    private async Task CommitBestEffortAsync(IRenderable renderable, string context, CancellationToken ct)
    {
        try
        {
            NoteTerminalCommitted();
            await _surface.CommitAsync(renderable, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "提交终端内容失败: {Context}", context);
        }
    }

    private async Task<string?> PromptTextAsync(string title, CancellationToken ct)
    {
        _inputRelay.BeginPrompt();
        try
        {
            return await _surface
                .PromptAsync(
                    (console, promptCt) => console.PromptAsync(new TextPrompt<string>(Markup.Escape(title)), promptCt),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _inputRelay.EndPrompt();
        }
    }

    private async Task<bool> PromptConfirmAsync(string title, CancellationToken ct)
    {
        _inputRelay.BeginPrompt();
        try
        {
            return await _surface
                .PromptAsync(
                    (console, promptCt) => console.PromptAsync(new ConfirmationPrompt(Markup.Escape(title)), promptCt),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _inputRelay.EndPrompt();
        }
    }

    private async Task<string?> PromptChoiceAsync(string title, IReadOnlyList<TuiCompletionItem> items, CancellationToken ct)
    {
        var choices = items.Select(i => i.Name).ToList();
        var displays = items.ToDictionary(
            i => i.Name,
            i => Markup.Escape($"{i.Name} · {i.Description}"),
            StringComparer.Ordinal);

        _inputRelay.BeginPrompt();
        try
        {
            var chosen = await _surface
                .PromptAsync(
                    (console, promptCt) => console.PromptAsync(
                        new SelectionPrompt<string>()
                            .Title(Markup.Escape(title))
                            .PageSize(15)
                            .AddChoices(choices)
                            .UseConverter(value => displays.TryGetValue(value, out var display) ? display : Markup.Escape(value))
                            // Esc 取消：返回哨兵值视为放弃（否则选择器无法退出）。
                            .AddCancelResult(CompletionCancelSentinel),
                        promptCt),
                    ct)
                .ConfigureAwait(false);

            return string.Equals(chosen, CompletionCancelSentinel, StringComparison.Ordinal) ? null : chosen;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _inputRelay.EndPrompt();
        }
    }

    private async Task HandleEventAsync(IMessageEvent evt, CancellationToken ct)
    {
        var context = _context;
        if (context is null)
            return;

        // stream.start 表示同一 (LoopId, Step) 被重开（重试）：账本必须同步重置，
        // 否则上一 attempt 的已固化内容会被永久重复，或新 attempt 从旧 offset 切片丢字。
        if (evt is StreamStartEvent streamStart)
            _ledger.Reset(StreamStartKey(streamStart));

        context.Interpreter.Apply(evt);

        if (evt is ToolCallEvent toolCall)
        {
            var block = context.State.Find($"tool:{toolCall.ToolCallId}");
            if (block?.Tool is not null)
                context.Tracker.Observe(block.Tool);
        }

        // command.result(NeedsRefresh)：服务端已变更会话内容（/clear、压缩等），重载快照并重建视图。
        if (evt is CommandResultEvent { NeedsRefresh: true } refreshEvt &&
            await RefreshFromSessionAsync(refreshEvt.SessionId, ct).ConfigureAwait(false))
        {
            _lastRenderAt = DateTime.MinValue;
            return;
        }

        if (await CommitTerminalBlocksAsync(ct).ConfigureAwait(false))
            _lastRenderAt = DateTime.MinValue;

        // 流式增量后固化稳定前缀，活动区只保留尾部，避免整帧溢出回退。
        if (evt is StreamDeltaEvent && await TryCommitStablePrefixesAsync(ct).ConfigureAwait(false))
            _lastRenderAt = DateTime.MinValue;

        await MaybeRenderAsync(false, ct).ConfigureAwait(false);
    }

    private async Task<bool> CommitTerminalBlocksAsync(CancellationToken ct)
    {
        var context = _context;
        if (context is null)
            return false;

        var committed = false;
        var width = GetWidth();
        foreach (var block in context.State.Blocks)
        {
            if (!block.IsTerminal)
                continue;

            block.IsStreaming = false;

            // 非 assistant 块（用户/系统/错误/工具/压缩）沿用一次性提交语义。
            if (block.Kind != TuiBlockKind.Assistant)
            {
                if (_ledger.IsCommitted(block.Key))
                    continue;

                if (await CommitBlockSafeAsync(block, width, ct).ConfigureAwait(false))
                {
                    _ledger.Advance(block.Key, block.Text?.Length ?? 0, false);
                    committed = true;
                }
                continue;
            }

            // assistant 块按「已固化长度」增量提交，允许同一 key 在文本变长或推理迟到时补写。
            if (await CommitAssistantTerminalAsync(
                    _ledger,
                    block,
                    b => CommitBlockSafeAsync(b, width, ct)).ConfigureAwait(false))
            {
                committed = true;
            }
        }

        return committed;
    }

    private async Task<bool> CommitBlockSafeAsync(TuiBlock block, int width, CancellationToken ct)
    {
        try
        {
            NoteTerminalCommitted();
            await _surface.CommitAsync(_renderer.BuildCommitted(block, width), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "固化块失败: {Key}", block.Key);
            return false;
        }
    }

    /// <summary>assistant 终态块固化计划：正文起点/增量长度 + 是否需要单独补写推理。</summary>
    internal readonly record struct TuiTerminalCommitPlan(int TextOffset, int TextLength, bool CommitReasoning);

    /// <summary>
    /// 依据「已固化长度」计算 assistant 终态块的增量提交计划：
    /// 已提交过的块若文本变长仍可补写尾部，推理未固化时单独补写，避免重复与丢字。
    /// </summary>
    /// <summary>执行中「Esc 二次确认取消」是否已武装且在确认窗口内（超时需重新按两次）。</summary>
    internal static bool IsCancelArmed(DateTime? armedAt, DateTime now, bool executing)
        => executing
           && armedAt is not null
           && now - armedAt.Value <= TimeSpan.FromMilliseconds(CancelConfirmWindowMs);

    /// <summary>状态栏提示：已武装等待第二次 Esc 时为取消提示，否则 null。</summary>
    private string? CancelHint
        => IsCancelArmed(_cancelArmedAt, DateTime.UtcNow, _context?.State.IsExecuting == true)
            ? "再按一次 Esc 取消执行"
            : null;

    internal static TuiTerminalCommitPlan PlanAssistantTerminalCommit(
        int textLength,
        int committedChars,
        bool hasReasoning,
        bool reasoningCommitted)
    {
        var offset = committedChars <= textLength ? committedChars : 0;
        return new TuiTerminalCommitPlan(offset, textLength - offset, hasReasoning && !reasoningCommitted);
    }

    /// <summary>
    /// 固化单个 assistant 终态块（增量正文 + 迟到推理单独补写）。
    /// <para>账本仅在提交成功后推进：<paramref name="commitAsync"/> 失败时保持可重试，不吞掉尾部。</para>
    /// </summary>
    internal static async Task<bool> CommitAssistantTerminalAsync(
        TuiCommitLedger ledger,
        TuiBlock block,
        Func<TuiBlock, Task<bool>> commitAsync)
    {
        var text = block.Text ?? string.Empty;
        var committedChars = ledger.GetCommittedChars(block.Key, text.Length);
        var plan = PlanAssistantTerminalCommit(
            text.Length,
            committedChars,
            !string.IsNullOrEmpty(block.Reasoning),
            ledger.IsReasoningCommitted(block.Key));

        if (plan.TextLength == 0 && !plan.CommitReasoning)
            return false;

        var wroteReasoning = false;
        var anyCommitted = false;

        // 1) 迟到的推理：单独提交一次仅含推理的块（Text 置空），再走正文。
        if (plan.CommitReasoning)
        {
            if (!await commitAsync(BuildReasoningCommitBlock(block)).ConfigureAwait(false))
                return false;

            wroteReasoning = true;
            anyCommitted = true;
        }

        // 2) 正文尾部（不携带推理，避免重复）。
        if (plan.TextLength > 0)
        {
            if (await commitAsync(BuildTerminalCommitBlock(block, plan.TextOffset)).ConfigureAwait(false))
            {
                anyCommitted = true;
            }
            else
            {
                // 正文失败：仅推进已成功的推理标记，正文偏移保持原值以便重试。
                if (wroteReasoning)
                    ledger.Advance(block.Key, committedChars, true);
                return anyCommitted;
            }
        }

        if (!anyCommitted)
            return false;

        ledger.Advance(block.Key, text.Length, wroteReasoning || ledger.IsReasoningCommitted(block.Key));
        return true;
    }

    /// <summary>构造终态 assistant 块的正文补写体：只含未固化尾部，<b>不携带推理</b>（推理由独立块补写）。</summary>
    internal static TuiBlock BuildTerminalCommitBlock(TuiBlock block, int offset)
        => block.Kind == TuiBlockKind.Assistant ? SliceForCommit(block, offset) : block;

    /// <summary>构造只含 <paramref name="block"/> 未固化尾部的浅拷贝；不携带推理，避免重复。</summary>
    private static TuiBlock SliceForCommit(TuiBlock block, int offset)
    {
        var text = block.Text ?? string.Empty;
        return new TuiBlock
        {
            Key = block.Key,
            Kind = block.Kind,
            LoopId = block.LoopId,
            Step = block.Step,
            Text = offset < text.Length ? text[offset..] : string.Empty,
            Reasoning = string.Empty,
            Title = block.Title,
            IsStreaming = false,
            IsCancelled = block.IsCancelled,
            IsTerminal = true,
            Tool = block.Tool,
        };
    }

    /// <summary>构造仅含推理的固化块（正文置空），用于迟到的推理单独补写。</summary>
    internal static TuiBlock BuildReasoningCommitBlock(TuiBlock block) => new()
    {
        Key = block.Key,
        Kind = block.Kind,
        LoopId = block.LoopId,
        Step = block.Step,
        Text = string.Empty,
        Reasoning = block.Reasoning,
        Title = block.Title,
        IsStreaming = false,
        IsCancelled = block.IsCancelled,
        IsTerminal = true,
        Tool = block.Tool,
    };

    /// <summary>
    /// 构造前缀固化浅拷贝：offset == 0（首个固化片）携带推理，与首段正文一并写入滚动历史；
    /// offset &gt; 0 时不携带推理，避免推理重复出现。
    /// </summary>
    internal static TuiBlock BuildPrefixCommitBlock(TuiBlock block, int offset, int cut)
    {
        var text = block.Text ?? string.Empty;
        return new TuiBlock
        {
            Key = block.Key,
            Kind = block.Kind,
            LoopId = block.LoopId,
            Step = block.Step,
            Text = text[offset..cut],
            Reasoning = offset == 0 ? block.Reasoning : string.Empty,
        };
    }

    /// <summary>流式增量后固化各非终态 assistant 块的稳定前缀到滚动历史。</summary>
    private async Task<bool> TryCommitStablePrefixesAsync(CancellationToken ct)
    {
        var context = _context;
        if (context is null)
            return false;

        var committed = false;
        var width = GetWidth();
        foreach (var block in context.State.Blocks)
        {
            if (block.IsTerminal || block.Kind != TuiBlockKind.Assistant || string.IsNullOrEmpty(block.Text))
                continue;

            if (await TryCommitStablePrefixAsync(block, width, ct).ConfigureAwait(false))
                committed = true;
        }

        return committed;
    }

    /// <summary>固化单个块的稳定前缀；先 Commit 成功再推进偏移，顺序不可颠倒。</summary>
    private async Task<bool> TryCommitStablePrefixAsync(TuiBlock block, int width, CancellationToken ct)
    {
        var text = block.Text;
        if (string.IsNullOrEmpty(text))
            return false;

        var offset = _ledger.GetCommittedChars(block.Key, text.Length);
        var cut = FindStableCut(text, offset);
        if (cut <= offset)
            return false;

        var copy = BuildPrefixCommitBlock(block, offset, cut);

        try
        {
            NoteTerminalCommitted();
            await _surface.CommitAsync(_renderer.BuildCommitted(copy, width), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "固化稳定前缀失败: {Key}", block.Key);
            return false;
        }

        // Commit 成功后才推进偏移：失败时下次可重试，偏移单调不减。
        // 首个固化片（offset == 0）已把推理写入滚动历史，一并标记避免终态补写重复。
        _ledger.Advance(block.Key, cut, offset == 0 && !string.IsNullOrEmpty(block.Reasoning));

        return true;
    }

    /// <summary>
    /// 查找可安全固化的文本前缀切点：切点只落在行边界，且不处于未闭合代码围栏内；
    /// 取「24 行上限 / 段落空行 / 闭合围栏行末尾」中最靠后者，找不到返回 <paramref name="fromOffset"/>。
    /// </summary>
    internal static int FindStableCut(string text, int fromOffset)
    {
        if (string.IsNullOrEmpty(text) || fromOffset >= text.Length)
            return fromOffset;

        // 预扫描围栏起点（单次 O(n)），供前向扫描维护围栏奇偶。
        var fenceStarts = new List<int>();
        var scan = 0;
        while (scan < text.Length)
        {
            var found = text.IndexOf("```", scan, StringComparison.Ordinal);
            if (found < 0)
                break;

            fenceStarts.Add(found);
            scan = found + 3;
        }

        var fenceIndex = 0;
        var fenceParity = 0;
        while (fenceIndex < fenceStarts.Count && fenceStarts[fenceIndex] < fromOffset)
        {
            fenceParity ^= 1;
            fenceIndex++;
        }

        var lineCut = 0;
        var paragraphCut = 0;
        var fenceCut = 0;
        var lineCount = 0;

        for (var i = fromOffset; i < text.Length; i++)
        {
            while (fenceIndex < fenceStarts.Count && fenceStarts[fenceIndex] == i)
            {
                fenceParity ^= 1;
                fenceIndex++;
            }

            if (text[i] != '\n')
                continue;

            var p = i + 1;
            lineCount++;

            if (fenceParity != 0)
                continue;

            if (lineCut == 0 && lineCount >= CommitLines)
                lineCut = p;

            if (p >= 2 && text[p - 2] == '\n')
                paragraphCut = p;

            if (IsFenceLineEnd(text, p))
                fenceCut = p;
        }

        // 文本末尾若非换行，仍可作为闭合围栏行末尾固化。
        if (text[^1] != '\n' && fenceParity == 0 && IsFenceLineEnd(text, text.Length))
            fenceCut = text.Length;

        var cut = Math.Max(lineCut, Math.Max(paragraphCut, fenceCut));
        return cut > fromOffset ? cut : fromOffset;
    }

    /// <summary>判断 <paramref name="endExclusive"/> 处结束的行是否为代码围栏行（前后缀围栏闭合）。</summary>
    private static bool IsFenceLineEnd(string text, int endExclusive)
    {
        var searchFrom = endExclusive - 2;
        var lineStart = searchFrom >= 0 ? text.LastIndexOf('\n', searchFrom) : -1;
        var line = text[(lineStart + 1)..endExclusive];
        return line.TrimStart().StartsWith("```", StringComparison.Ordinal);
    }

    private async Task PromptPendingAsync(CancellationToken ct)
    {
        if (_context is null || ct.IsCancellationRequested)
            return;

        // 请求被解决/移除后清理「已忽略」记录，使同一请求下次触发时可再次呈现。
        PruneDismissedPermissions();

        var permission = _permissions.Pending.FirstOrDefault();
        if (permission is not null && !IsDismissed(permission))
        {
            _inputRelay.BeginPrompt();
            PermissionPromptResult? decision;
            try
            {
                decision = await PermissionPrompt
                    .ShowAsync(_surface, permission, BuildOwnerLabel(permission.SessionId), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 停机：交给上层收敛（不在此处把请求判成「用户取消」）。
                throw;
            }
            catch (OperationCanceledException)
            {
                // 提示被 Esc 取消（TextPrompt 不响应 Esc，由渲染端取消令牌）：按「不决策」处理。
                decision = null;
            }
            catch (Exception ex)
            {
                // 提示自身失败不得终止引擎：按「已忽略」收敛（请求仍挂起，可由下次触发重试）。
                _logger?.LogError(ex, "权限提示失败，按忽略处理: {RequestId}", permission.RequestId);
                decision = null;
            }
            finally
            {
                _inputRelay.EndPrompt();
            }

            if (decision is not null)
            {
                _permissions.TryResolve(permission, decision.Effect, decision.Scope);
            }
            else if (permission.RequestId is not null)
            {
                // 用户取消（Esc）：记录已忽略，避免每 60ms 反复弹出难以关闭；请求仍挂起，可由下次触发重试。
                _dismissedPermissionRequests.Add(permission.RequestId);
                await CommitNoticeAsync("已忽略该请求，可在下次触发时重试", ct).ConfigureAwait(false);
            }

            _lastRenderAt = DateTime.MinValue;
            return;
        }

        var question = _questions.Pending.FirstOrDefault();
        if (question is not null)
        {
            _inputRelay.BeginPrompt();
            QuestionResult? result;
            try
            {
                result = await QuestionPrompt.ShowAsync(_surface, question, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // 提示被 Esc 取消：按「不决策」处理，下方统一收敛为「已取消作答」。
                result = null;
            }
            catch (Exception ex)
            {
                // 提示自身失败不得终止引擎：按「用户取消作答」收敛，
                // 否则请求会一直挂起并由主循环每轮重试（表现为反复弹不出提示 + 日志刷屏）。
                _logger?.LogError(ex, "问答提示失败，按取消处理: {RequestId}", question.Id);
                result = null;
            }
            finally
            {
                _inputRelay.EndPrompt();
            }

            if (result is not null)
            {
                _questions.TryResolve(question, result);
            }
            else
            {
                // 用户按 Esc（或提示内部取消）放弃作答：以 Cancelled 立即收敛，
                // 把控制权交回聊天窗口，而不是让请求挂起到超时（否则会反复弹窗）。
                _questions.TryResolve(question, new QuestionResult
                {
                    RequestId = question.Id,
                    Status = QuestionResultStatus.Cancelled,
                    Answers = [],
                });

                if (!ct.IsCancellationRequested)
                    await CommitNoticeAsync("已取消作答", ct).ConfigureAwait(false);
            }

            _lastRenderAt = DateTime.MinValue;
        }
    }

    /// <summary>请求仍挂起但用户已取消呈现：不再重复弹窗，直到它被解决或被移除。</summary>
    private bool IsDismissed(PermissionRequest request)
        => request.RequestId is not null && _dismissedPermissionRequests.Contains(request.RequestId);

    private void PruneDismissedPermissions()
    {
        if (_dismissedPermissionRequests.Count == 0)
            return;

        var pending = _permissions.Pending;
        _dismissedPermissionRequests.RemoveWhere(id =>
            !pending.Any(p => string.Equals(p.RequestId, id, StringComparison.Ordinal)));
    }

    private string BuildOwnerLabel(string? sessionId)
    {
        var active = _context?.SessionId;
        return string.IsNullOrEmpty(sessionId) || string.Equals(sessionId, active, StringComparison.Ordinal)
            ? $"主会话 · {active ?? "-"}"
            : $"子代理/后台会话 · {sessionId}";
    }

    private async Task MaybeRenderAsync(bool force, CancellationToken ct, bool tickOnly = false)
    {
        var context = _context;
        if (context is null)
            return;

        var chars = CountActiveChars(context.State);
        var threshold = IsInCodeBlock(context.State) ? RedrawCharsCode : RedrawCharsNormal;
        var elapsed = (DateTime.UtcNow - _lastRenderAt).TotalMilliseconds;

        if (!force && elapsed < RedrawIntervalMs && chars - _lastRenderedChars < threshold)
            return;

        // 周期 tick 的补绘走脏检查：tick 每 60ms 都会尝试重绘（流式内容的节流补绘依赖它），
        // 但空闲时重写整帧会把终端宿主画在物理光标处的输入法组合串擦掉 → 输入中文时持续闪屏。
        if (tickOnly && !force && !ShouldRenderOnTick(_lastRenderedTick, TakeTickSnapshot(context)))
            return;

        await RenderAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 周期 tick 是否需要重绘（纯函数，供回归）：与「上次已渲染」的快照不同即需要。
    /// </summary>
    internal static bool ShouldRenderOnTick(TickRenderSnapshot last, TickRenderSnapshot current)
        => !last.Equals(current);

    /// <summary>
    /// 采集 tick 判据快照（tick 判定与渲染后记录共用同一口径，避免两处漂移）。
    /// <para>
    /// 顺带把状态栏外部数据写入视图态（<see cref="ApplyStatusLine"/>）：提前取值与渲染时再取一次等价。
    /// </para>
    /// </summary>
    private TickRenderSnapshot TakeTickSnapshot(SessionContext context)
        => new(
            CountActiveChars(context.State),
            context.State.IsExecuting,
            CountBackgroundExecutions(context.SessionId),
            CurrentPendingCount(),
            ApplyStatusLine(context),
            GetWidth(),
            GetRenderHeight(),
            _terminalCommits);

    /// <summary>
    /// 记录一次「活动区外写入」（固化提交）。提交会退出活动区（Live <c>AutoClear</c> 即擦除整块），
    /// 故下一帧必须重绘，否则输入行与状态栏会一直空着；周期 tick 的脏检查用代次捕获这一点。
    /// 提交可能失败，但失败多记一次只会多绘一帧，无害。
    /// </summary>
    private void NoteTerminalCommitted() => _terminalCommits++;

    private async Task RenderAsync(CancellationToken ct)
    {
        var context = _context;
        if (context is null)
            return;

        var pendingApprovals = CurrentPendingCount();
        _ledger.Prune(context.State);
        var status = ApplyStatusLine(context);
        var active = _renderer.BuildActiveViewWithCaret(
            context.State,
            _editor,
            GetWidth(),
            pendingApprovals,
            CountBackgroundExecutions(context.SessionId),
            _ledger.Offsets,
            Math.Max(0, GetRenderHeight() - 1),
            GetCompletionCandidates(),
            CancelHint);
        // 防御性：活动区更新失败不得中断主循环（当前实现不抛，替换实现时仍需保证）。
        try
        {
            await _surface.UpdateAsync(active.View, active.Caret, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "活动区更新失败");
        }

        _lastRenderedChars = CountActiveChars(context.State);
        // 记录「本帧实际用的」判据值（而非事后重读，避免与帧内容不一致导致下一次 tick 白重绘）。
        _lastRenderedTick = new TickRenderSnapshot(
            _lastRenderedChars,
            context.State.IsExecuting,
            CountBackgroundExecutions(context.SessionId),
            pendingApprovals,
            status,
            GetWidth(),
            GetRenderHeight(),
            _terminalCommits);
        _lastRenderAt = DateTime.UtcNow;
    }

    /// <summary>在途权限请求 + 问答请求数（状态栏「待批」）。</summary>
    private int CurrentPendingCount()
        => (_permissions.Pending?.Count ?? 0) + (_questions.Pending?.Count ?? 0);

    /// <summary>
    /// 读取状态栏外部数据（工作目录、审批模式、上下文用量）写入视图态，并返回本次快照。
    /// <para>
    /// 每帧取值：工作目录可能因切根而变，全局审批开关可热重载，用量由 TokenBudget Hook 异步更新，
    /// 缓存反而会显示过期值；返回的快照供周期 tick 的脏检查比对（见 <see cref="ShouldRenderOnTick"/>）。
    /// </para>
    /// </summary>
    private StatusSignature ApplyStatusLine(SessionContext context)
    {
        try
        {
            var signature = new StatusSignature(
                ToBudget(_budgetNotifier?.GetCurrentStatus(context.SessionId)),
                ResolveGlobalAutoApprove(_seeingOptions),
                ResolveWorkspaceRoot(_workspace));

            context.State.WorkspaceRoot = signature.WorkspaceRoot;
            context.State.GlobalAutoApprove = signature.GlobalAutoApprove;
            context.State.Budget = signature.Budget;
            return signature;
        }
        catch (Exception ex)
        {
            // 状态栏是装饰性信息，任何异常都不得影响渲染主循环；读取失败视为「无变化」。
            _logger?.LogDebug(ex, "状态栏数据刷新失败");
            return new StatusSignature(
                context.State.Budget,
                context.State.GlobalAutoApprove,
                context.State.WorkspaceRoot);
        }
    }

    /// <summary>全局审批开关（<c>Permission.AutoApproveAll</c>，热重载配置）；取不到时视为关闭。</summary>
    internal static bool ResolveGlobalAutoApprove(IOptionsMonitor<SeeingAgentOptions>? options)
    {
        try
        {
            return options?.CurrentValue.Permission?.AutoApproveAll ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前工作目录：优先项目根（<c>{root}/.seeing</c> 的父目录），不可用时回落进程 cwd。</summary>
    internal static string ResolveWorkspaceRoot(IWorkspaceProvider? workspace)
    {
        try
        {
            var root = workspace?.GetProjectRoot();
            if (!string.IsNullOrWhiteSpace(root))
                return root!;
        }
        catch
        {
            // 工作区未初始化等情况：回落 cwd
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>TokenBudget 快照 → 状态栏上下文用量；无数据时返回 null（不显示该段）。</summary>
    internal static TuiBudget? ToBudget(Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse? status)
    {
        if (status is null || (status.CurrentTokens <= 0 && status.MaxTokens <= 0))
            return null;

        return new TuiBudget(
            status.CurrentTokens,
            status.MaxTokens > 0 ? status.MaxTokens : null);
    }

    /// <summary>行首斜杠命令 token 的候选（上限 <see cref="TuiRenderOptions.MaxCompletionRows"/>）；否则空。</summary>
    private IReadOnlyList<TuiCompletionItem> GetCompletionCandidates()
    {
        try
        {
            var items = _completion.GetCompletions(_editor.Text, _editor.Cursor);
            if (items.Count == 0)
                return [];

            var max = Math.Max(1, _renderOptions.MaxCompletionRows);
            return items.Count <= max ? items : items.Take(max).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "计算补全候选失败");
            return [];
        }
    }

    /// <summary>统计「可呈现会话」中除主会话外仍有未终态执行的会话数（来源可靠，不编造）。</summary>
    private int CountBackgroundExecutions(string activeSessionId)
    {
        try
        {
            var count = 0;
            foreach (var sessionId in _surfaceProvider.SurfaceSessionIds)
            {
                if (string.IsNullOrEmpty(sessionId) || string.Equals(sessionId, activeSessionId, StringComparison.Ordinal))
                    continue;

                if (_executionStatus.GetOverview(sessionId).HasActiveExecution)
                    count++;
            }

            return count;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "统计后台执行数失败");
            return 0;
        }
    }

    private int GetWidth()
    {
        var width = _surface.Console.Profile.Width;
        return width > 0 ? width : 80;
    }

    /// <summary>可用终端高度（<=0 表示不可知，返回 0 交由渲染端不裁剪）。</summary>
    private int GetRenderHeight()
    {
        var height = _surface.Console.Profile.Height;
        return height > 0 ? height : 0;
    }

    private static long CountActiveChars(TuiViewState state)
    {
        long total = 0;
        foreach (var block in state.Blocks)
        {
            if (block.IsTerminal)
                continue;

            total += block.Text?.Length ?? 0;
            total += block.Reasoning?.Length ?? 0;
        }

        return total;
    }

    private static bool IsInCodeBlock(TuiViewState state)
    {
        foreach (var block in state.Blocks)
        {
            if (block.IsTerminal || block.Kind != TuiBlockKind.Assistant || string.IsNullOrEmpty(block.Text))
                continue;

            if ((CountOccurrences(block.Text, "```") & 1) == 1)
                return true;
        }

        return false;
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static async Task<TuiKeyInput?> ReadKeyAsync(ChannelReader<TuiKeyInput> reader, CancellationToken ct)
    {
        try
        {
            if (await reader.WaitToReadAsync(ct).ConfigureAwait(false) && reader.TryRead(out var key))
                return key;
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    private static async Task<IMessageEvent?> ReadEventAsync(ChannelReader<IMessageEvent> reader, CancellationToken ct)
    {
        try
        {
            if (await reader.WaitToReadAsync(ct).ConfigureAwait(false) && reader.TryRead(out var evt))
                return evt;
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    /// <summary>
    /// 主循环本轮等待的任务集：事件源结束进入无事件模式（<paramref name="eventTask"/> 为 null）后只等输入与 ticker，
    /// 既不把 null 传给 <see cref="Task.WhenAny(Task[])"/>，也不会每轮立即命中已完成任务空转。
    /// </summary>
    internal static Task[] BuildWaitSet(Task inputTask, Task? eventTask, Task tickTask)
        => eventTask is null ? [inputTask, tickTask] : [inputTask, eventTask, tickTask];

    /// <summary>事件读取结果为 null 表示事件源已结束（泵通道完成），应进入无事件模式而非重建读取任务。</summary>
    internal static bool ShouldEnterNoEventMode(IMessageEvent? evt) => evt is null;

    /// <summary>事件泵通道已完成（无事件模式）时需要按需重建事件源，否则再提交后事件永不渲染。</summary>
    internal static bool RequiresEventSourceRebuild(bool pumpCompleted) => pumpCompleted;

    /// <summary>
    /// 事件源重建退避判定：失败次数越多退避越久（指数，封顶 <see cref="RebuildBackoffMaxMs"/> 毫秒）；
    /// 未失败时立即可重试。用于避免「每次输入即重建、重建即失败」的高频重试与 100% CPU。
    /// </summary>
    internal static bool ShouldAttemptEventSourceRebuild(DateTime lastFailureAt, DateTime now, int failureCount)
    {
        if (failureCount <= 0)
            return true;

        var shift = Math.Min(failureCount, MaxRebuildBackoffShift);
        var backoffMs = Math.Min((long)RebuildBackoffBaseMs << shift, RebuildBackoffMaxMs);
        return (now - lastFailureAt).TotalMilliseconds >= backoffMs;
    }

    /// <summary>事件源重建结果 → 退避状态：成功清零（事件源恢复），失败累积并记录时间。</summary>
    internal static (int FailureCount, DateTime LastFailureAt) UpdateRebuildBackoff(
        int failureCount, DateTime lastFailureAt, bool success, DateTime now)
        => success ? (0, DateTime.MinValue) : (failureCount + 1, now);

    /// <summary>复算 stream.start 对应的 assistant 块 Key（与 <see cref="TuiEventInterpreter"/> 的规则一致）。</summary>
    internal static string StreamStartKey(StreamStartEvent evt)
        => TuiViewState.AssistantKey(evt.LoopId, evt.Step, $"step{evt.Step}");

    /// <summary>
    /// command.result(NeedsRefresh) 的会话重载：读回会话快照（写回存储读己所写），重建视图并清空账本。
    /// <para>返回 false 表示快照不可读或非当前会话，调用方回退到常规增量提交。</para>
    /// </summary>
    private async Task<bool> RefreshFromSessionAsync(string sessionId, CancellationToken ct)
    {
        var context = _context;
        if (context is null || string.IsNullOrEmpty(sessionId) ||
            !string.Equals(sessionId, context.SessionId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var session = await _sessionStore.LoadAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null)
                return false;

            context.State.ResetFromSession(session);
            _ledger.Clear();
            _lastRenderedChars = 0;
            _lastRenderAt = DateTime.MinValue;

            await RenderAsync(ct).ConfigureAwait(false);
            await CommitTerminalBlocksAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "重载会话快照失败: {SessionId}", sessionId);
            return false;
        }
    }

    private async Task ShutdownAsync()
    {
        if (_inputThread is not null)
        {
            try
            {
                await _inputThread.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "停止输入线程失败");
            }

            _inputThread = null;
        }

        if (_context is not null)
        {
            try
            {
                await _context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "停止事件泵失败");
            }

            _context = null;
        }

        try
        {
            await _submitter.CancelAllInFlightAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "取消在途执行失败");
        }

        if (_permissionRegistered)
        {
            try
            {
                _permissionRegistry.Unregister(_surfaceProvider);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "注销权限呈现提供方失败");
            }

            _permissionRegistered = false;
        }

        if (_questionRegistered)
        {
            try
            {
                _questionRegistry.Unregister(_surfaceProvider);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "注销问答呈现提供方失败");
            }

            _questionRegistered = false;
        }

        try
        {
            if (_sessionStore is IPersistenceFlusher sessionFlusher)
                await sessionFlusher.FlushAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "刷新会话存储失败");
        }

        try
        {
            if (_groupStore is IPersistenceFlusher groupFlusher)
                await groupFlusher.FlushAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "刷新会话组存储失败");
        }

        try
        {
            await _surface.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "停止终端出口失败");
        }
    }

    /// <summary>待发附件：协议附件 + 输入行展示串。</summary>
    private sealed record PendingAttachment(TuiAttachment Attachment, string Display);

    private sealed class SessionContext : IAsyncDisposable
    {
        public SessionContext(
            TuiViewState state,
            TuiEventInterpreter interpreter,
            TuiTaskTracker tracker,
            ITuiEventPump pump)
        {
            State = state;
            Interpreter = interpreter;
            Tracker = tracker;
            _pump = pump;
        }

        public TuiViewState State { get; }

        public TuiEventInterpreter Interpreter { get; }

        public TuiTaskTracker Tracker { get; }

        public ITuiEventPump Pump => _pump;

        public string SessionId => State.SessionId;

        /// <summary>替换事件泵并返回旧泵（调用方负责释放）；供「泵已完成」时的按需重建。</summary>
        public ITuiEventPump ReplacePump(ITuiEventPump pump)
        {
            var previous = _pump;
            _pump = pump;
            return previous;
        }

        private ITuiEventPump _pump;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Pump.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // 停泵失败不阻断释放
            }

            try
            {
                await Pump.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放失败不阻断
            }

            try
            {
                await Tracker.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放失败不阻断
            }
        }
    }
}

/// <summary>
/// 流式固化账本（引擎内部状态，可脱离引擎单测）。
/// <para><see cref="IsCommitted"/> 仅表示「该块提交过」，<b>不是</b>「永不再提交」的硬闸：
/// 块文本变长或推理迟到时仍按已固化长度增量补写（见 TuiChatEngine.CommitTerminalBlocksAsync）。</para>
/// </summary>
internal sealed class TuiCommitLedger
{
    private readonly HashSet<string> _committed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _chars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reasoning = new(StringComparer.Ordinal);

    /// <summary>已固化正文偏移（供活动区渲染裁剪）。</summary>
    public IReadOnlyDictionary<string, int> Offsets => _chars;

    /// <summary>已固化正文偏移；超出当前文本长度时视为 0，避免旧偏移切片越界。</summary>
    public int GetCommittedChars(string key, int textLength)
        => _chars.TryGetValue(key, out var value) && value <= textLength ? value : 0;

    public bool IsCommitted(string key) => _committed.Contains(key);

    public bool IsReasoningCommitted(string key) => _reasoning.Contains(key);

    /// <summary>流式块重置（如 stream.start 重发）：清除该 key 的全部固化记录，允许从头重提交。</summary>
    public void Reset(string key)
    {
        _committed.Remove(key);
        _chars.Remove(key);
        _reasoning.Remove(key);
    }

    public void Clear()
    {
        _committed.Clear();
        _chars.Clear();
        _reasoning.Clear();
    }

    /// <summary>提交成功后推进：正文偏移置为当前长度并标记已提交；已写入推理时标记。</summary>
    public void Advance(string key, int textLength, bool reasoningCommitted)
    {
        _chars[key] = textLength;
        _committed.Add(key);
        if (reasoningCommitted)
            _reasoning.Add(key);
    }

    /// <summary>丢弃超过块当前文本长度的已固化偏移（块被重置后旧偏移失效），推理标记一并失效。</summary>
    public void Prune(TuiViewState state)
    {
        foreach (var block in state.Blocks)
        {
            if (_chars.TryGetValue(block.Key, out var offset) && offset > (block.Text?.Length ?? 0))
            {
                _chars.Remove(block.Key);
                _reasoning.Remove(block.Key);
            }
        }
    }
}

/// <summary>
/// TUI 预算状态通知器：缓存每个会话的最新 Budget 快照并支持订阅。
/// <para>
/// 状态栏按帧拉取 <see cref="GetCurrentStatus"/>（单活跃会话，无需推送）；
/// <see cref="Publish"/> 由 TokenBudget Hook 在 taskpool 线程调用，与渲染线程并发，
/// 故读写统一加锁。
/// </para>
/// </summary>
public sealed class TuiBudgetStatusNotifier : Seeing.Agent.TokenBudget.IBudgetStatusNotifier
{
    private readonly Dictionary<string, Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse>>> _subscribers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IDisposable Subscribe(string sessionId, Action<Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse> onUpdate)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);

        lock (_gate)
        {
            if (!_subscribers.TryGetValue(sessionId, out var list))
                _subscribers[sessionId] = list = [];

            list.Add(onUpdate);

            // 订阅即回放当前快照，避免订阅者等到下一次 Publish 才有数据。
            if (_current.TryGetValue(sessionId, out var status))
                SafeInvoke(onUpdate, status);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (!_subscribers.TryGetValue(sessionId, out var list))
                    return;

                list.Remove(onUpdate);
                if (list.Count == 0)
                    _subscribers.Remove(sessionId);
            }
        });
    }

    public void Publish(string sessionId, Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse status)
    {
        if (string.IsNullOrEmpty(sessionId) || status is null)
            return;

        List<Action<Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse>>? subscribers;
        lock (_gate)
        {
            _current[sessionId] = status;
            subscribers = _subscribers.TryGetValue(sessionId, out var list) ? list.ToList() : null;
        }

        // 锁外回调：订阅者异常不得影响 TokenBudget Hook。
        foreach (var subscriber in subscribers ?? [])
            SafeInvoke(subscriber, status);
    }

    public Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse? GetCurrentStatus(string sessionId)
    {
        lock (_gate)
            return _current.TryGetValue(sessionId, out var status) ? status : null;
    }

    private static void SafeInvoke(Action<Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse> callback, Seeing.Agent.TokenBudget.Api.Responses.BudgetStatusResponse status)
    {
        try
        {
            callback(status);
        }
        catch
        {
            // 忽略订阅者异常
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            unsubscribe();
        }
    }
}

