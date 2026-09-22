using System.Threading.Channels;
using Seeing.Agent.Tui.Input;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 基于 Spectre <see cref="LiveDisplay"/> 委托模型的终端出口。
/// 持有唯一渲染线程；Update 合并、Commit/Prompt 不丢；每轮新建 LiveDisplay。
/// </summary>
public sealed class SpectreTerminalSurface : ITerminalSurface
{
    private const int MaxConsecutiveRenderFailures = 10;
    private const int FailureBackoffMs = 25;

    /// <summary>
    /// 终端「恢复光标可见」序列。关闭 bracketed paste（<c>\x1b[?2004l</c>）由 <see cref="RawInputReader"/> 负责，
    /// 此处不重复写出同一序列。
    /// </summary>
    internal const string TerminalRestoreSequence = "\u001b[?25h";

    private readonly IAnsiConsole _console;
    private readonly ChannelAnsiConsoleInput? _promptInput;
    private readonly Channel<RenderCommand> _commands;
    private readonly object _pendingGate = new();
    private readonly object _startGate = new();

    private RenderCommand? _pendingUpdate;
    private IRenderable _currentView = new Text(string.Empty);
    private Thread? _thread;
    private volatile bool _started;
    private volatile bool _stopRequested;
    private volatile bool _faulted;

    // 在途提示（渲染线程赋值/清空，StopAsync 跨线程读取以取消它）；同一时刻至多一个。
    private volatile PromptCommand? _activePrompt;
    private int _terminalRestored;

    public SpectreTerminalSurface()
        : this(AnsiConsole.Console)
    {
    }

    /// <summary>
    /// 提示输入取自 TUI 按键通道：用装饰器覆盖全局 <see cref="IAnsiConsole"/> 的 <c>Input</c>，
    /// 避免 Spectre 提示与 <see cref="RawInputReader"/> 争抢 stdin。
    /// </summary>
    public SpectreTerminalSurface(ChannelAnsiConsoleInput promptInput)
        : this(AnsiConsole.Console, promptInput)
    {
    }

    public SpectreTerminalSurface(IAnsiConsole console)
        : this(console, null)
    {
    }

    private SpectreTerminalSurface(IAnsiConsole console, ChannelAnsiConsoleInput? promptInput)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = promptInput is null ? console : new InputOverrideConsole(console, promptInput);
        _promptInput = promptInput;
        _commands = Channel.CreateBounded<RenderCommand>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public IAnsiConsole Console => _console;

    public Task UpdateAsync(IRenderable view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        EnsureStarted();

        if (_faulted)
            return Task.CompletedTask;

        PostUpdate(view);
        return Task.CompletedTask;
    }

    public Task CommitAsync(IRenderable committed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(committed);

        // 失效判定优先于 EnsureStarted：此时投递必然被丢弃，快速失败且不启动渲染线程。
        if (_faulted)
            throw ResolveCommitFailure(faulted: true, delivered: false)!;

        EnsureStarted();

        // 投递失败（有界等待超时）不得静默丢弃：抛出可辨识异常，调用方据此保留账本可重试。
        if (!TryPostCommit(committed))
            throw ResolveCommitFailure(faulted: false, delivered: false)!;

        return Task.CompletedTask;
    }

    public async Task<T> PromptAsync<T>(Func<IAnsiConsole, CancellationToken, Task<T>> prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        EnsureStarted();

        if (_faulted)
            throw new InvalidOperationException("终端渲染线程已失效，无法交互。");

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 提示取消源与调用方 ct（引擎停止令牌）链动：StopAsync 取消它即可让渲染线程立即退出提示；
        // 引擎令牌取消时同样触发，避免停机时长时间等待真按键。
        // 该源同时经 EscapeCancellationScope 被 Esc 触发，并作为「提示令牌」交给提示实现，
        // 使底层 Spectre 提示真正观察到取消（而不是被 WaitAsync 丢弃在后台继续吞按键）。
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var command = new PromptCommand(
            async (console, promptCt) => (object?)await prompt(console, promptCt).ConfigureAwait(false),
            completion,
            cancellation);

        if (!PostNonUpdate(command))
        {
            cancellation.Dispose();
            completion.TrySetException(new InvalidOperationException("终端渲染线程无响应，无法交互。"));
        }

        // 再与调用方 ct 竞速：命令尚在通道未被渲染线程消费时，仅靠 RunPrompt 无法观察到取消，
        // 此处确保停机令牌取消时调用方不会永久等待（残留命令由 StopAsync 兜底完成）。
        var result = await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        return (T)result!;
    }

    public async Task StopAsync()
    {
        if (!_started)
            return;

        _stopRequested = true;

        // 先终止在途提示：渲染线程若正阻塞在 RunPrompt（等待按键），Join 会超时，
        // 超时后 RestoreTerminal 会与该提示后续写终端并发。取消提示取消源让 RunPrompt 立即返回。
        CancelActivePrompt();

        if (!_faulted)
            TryPostStop();

        var thread = _thread;
        if (thread is not null)
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        // 兜底：线程退出后通道内可能仍残留未消费的提示命令，完成它们避免调用方永久等待。
        DrainRemainingPromptCommands();

        // 兜底：Join 超时（线程仍卡在未知路径）也执行一次。幂等，重复调用安全。
        RestoreTerminal();
    }

    /// <summary>完成通道内残留的提示命令（停止兜底）；非提示命令直接丢弃。</summary>
    private void DrainRemainingPromptCommands()
    {
        while (_commands.Reader.TryRead(out var leftover))
        {
            if (leftover is PromptCommand prompt)
            {
                prompt.Completion.TrySetCanceled(prompt.Cancellation.Token);
                prompt.Cancellation.Dispose();
            }
        }
    }

    /// <summary>取消在途提示；已结束/未登记时为空操作。</summary>
    private void CancelActivePrompt()
    {
        var prompt = _activePrompt;
        if (prompt is null)
            return;

        try
        {
            prompt.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 提示已结束并释放取消源：竞态无害。
        }
    }

    private void EnsureStarted()
    {
        if (_started)
            return;

        lock (_startGate)
        {
            if (_started)
                return;

            _thread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "tui-render",
            };
            _started = true;
            _thread.Start();
        }
    }

    private void PostUpdate(IRenderable view)
    {
        lock (_pendingGate)
            _pendingUpdate = new UpdateCommand(view);

        TryFlushPending();
    }

    /// <summary>投递固化命令；成功返回 true（渲染线程无响应时投递超时返回 false）。</summary>
    private bool TryPostCommit(IRenderable renderable) => PostNonUpdate(new CommitCommand(renderable));

    /// <summary>
    /// 投递失败时构造可辨识异常：已失效 → <see cref="InvalidOperationException"/>；
    /// 未投递（渲染线程无响应超时）→ <see cref="TimeoutException"/>；投递成功 → null。
    /// 纯函数，供单测（不启动渲染线程、不等待终端）。
    /// </summary>
    internal static Exception? ResolveCommitFailure(bool faulted, bool delivered)
    {
        if (faulted)
            return new InvalidOperationException("终端渲染线程已失效，提交被丢弃。");

        return delivered ? null : new TimeoutException("终端渲染线程无响应，提交被丢弃。");
    }

    /// <summary>投递命令；成功返回 true。写入有上限等待，避免渲染线程失效时调用方永久挂起。</summary>
    private bool PostNonUpdate(RenderCommand command)
    {
        FlushPendingBounded();
        return TryWriteBounded(command);
    }

    private void TryFlushPending()
    {
        lock (_pendingGate)
        {
            if (_pendingUpdate is null)
                return;

            if (_commands.Writer.TryWrite(_pendingUpdate))
                _pendingUpdate = null;
        }
    }

    private void FlushPendingBounded()
    {
        RenderCommand? pending;
        lock (_pendingGate)
        {
            pending = _pendingUpdate;
            _pendingUpdate = null;
        }

        if (pending is not null)
            TryWriteBounded(pending);
    }

    /// <summary>停止时非阻塞投递：通道满也无需等待，主循环会在消费完当前命令后观察到停止标志。</summary>
    private void TryPostStop()
    {
        RenderCommand? pending;
        lock (_pendingGate)
        {
            pending = _pendingUpdate;
            _pendingUpdate = null;
        }

        if (pending is not null)
            _commands.Writer.TryWrite(pending);

        _commands.Writer.TryWrite(new StopCommand());
    }

    private bool TryWriteBounded(RenderCommand command, int timeoutMs = 2000)
    {
        if (_commands.Writer.TryWrite(command))
            return true;

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            // sync-over-async（有界等待）：调用方为引擎线程/提示提交线程；通道容量为 1，渲染线程持续消费，
            // 正常路径 TryWrite 即成功、不会等待。渲染线程失效时以 timeoutMs 上限兜底返回 false，不永久挂起。
            // 当前渲染线程不反向等待引擎线程（引擎投递命令后立即返回、不等待处理完成），故无互等环；
            // 若未来引入「非引擎线程提交且渲染线程反过来等待提交方完成」的链路，需重新评估此同步等待。
            _commands.Writer.WriteAsync(command, cts.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>渲染线程判废：唤醒所有等待中的提示并置失效，后续调用快速失败而非挂起。</summary>
    internal void Fault()
    {
        _faulted = true;
        _stopRequested = true;

        while (_commands.Reader.TryRead(out var leftover))
        {
            if (leftover is PromptCommand dropped)
            {
                dropped.Completion.TrySetException(new InvalidOperationException("终端渲染线程已失效，无法交互。"));
                dropped.Cancellation.Dispose();
            }
        }

        lock (_pendingGate)
            _pendingUpdate = null;
    }

    /// <summary>异常退避期间排空一个已投递命令，避免通道占满导致生产者阻塞。</summary>
    private void DrainOneCommand()
    {
        if (!_commands.Reader.TryRead(out var dropped))
            return;

        if (dropped is PromptCommand pending)
        {
            pending.Completion.TrySetException(new InvalidOperationException("终端渲染线程异常，交互已取消。"));
            pending.Cancellation.Dispose();
        }
    }

    private void RenderLoop()
    {
        var consecutiveFailures = 0;
        while (!_stopRequested)
        {
            CommitCommand? commit = null;
            PromptCommand? prompt = null;

            try
            {
                // 活动区高度由渲染端（TailClipRenderable + TuiChatEngine 预算）保证 ≤ 终端高度，
                // 故不再需要「溢出即固化整帧」的兜底；Live 帧退出即擦除，固化内容随后单写一次。
                var live = _console.Live(_currentView)
                    .AutoClear(true)
                    .Overflow(VerticalOverflow.Crop)
                    .Cropping(VerticalOverflowCropping.Top);

                live.Start(ctx =>
                {
                    while (true)
                    {
                        TryFlushPending();

                        if (_stopRequested)
                            return;

                        // sync-over-async：本行位于渲染线程（Live 委托内），是命令通道的唯一读者；
                        // 引擎线程只投递、不等待处理完成，故此处同步等待不会与引擎形成互等环。
                        // 若未来有非引擎线程在持有引擎锁时等待渲染完成，需改为异步链重新评估。
                        var command = _commands.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
                        switch (command)
                        {
                            case UpdateCommand update:
                                _currentView = update.View;
                                ctx.UpdateTarget(update.View);
                                ctx.Refresh();
                                break;

                            case CommitCommand c:
                                commit = c;
                                // 提交前把活动区置空：Refresh 只画空白，避免重绘整帧后再擦除的闪烁。
                                _currentView = new Text(string.Empty);
                                ctx.UpdateTarget(_currentView);
                                return;

                            case PromptCommand p:
                                prompt = p;
                                return;

                            case StopCommand:
                                _stopRequested = true;
                                return;
                        }
                    }
                });
                consecutiveFailures = 0;
            }
            catch
            {
                // Live 异常不得导致空转：异常路径不消费命令，若继续循环会 100% CPU 且调用方永久挂起。
                if (commit is null && prompt is null)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaxConsecutiveRenderFailures)
                    {
                        Fault();
                        break;
                    }

                    DrainOneCommand();
                    Thread.Sleep(FailureBackoffMs);
                    continue;
                }
            }

            if (_stopRequested)
            {
                // 停止时若本回合已取出提示，必须完成它，否则调用方 await completion 永久挂起。
                if (prompt is not null)
                    SkipPrompt(prompt);
                break;
            }

            if (commit is not null)
            {
                try
                {
                    _console.Write(commit.Renderable);
                    // Write(IRenderable) 不写结尾换行；不补换行则下一帧 Live 会追加到同一行，
                    // 并使 RestoreCursor 的行数推算错位、残留上一帧内容。
                    _console.WriteLine();
                }
                catch
                {
                    // 固化失败不影响活动区重启。
                }
            }

            if (prompt is not null)
            {
                // 先登记在途提示再判定停止：与 StopAsync 的 _stopRequested/CancelActivePrompt 形成先后关系，
                // 避免「StopAsync 已读过 _activePrompt 为空 → RunPrompt 才登记」的取消漏网窗口。
                _activePrompt = prompt;
                if (ShouldSkipPrompt(_stopRequested))
                {
                    // 已请求停止：不进入提示，直接以「已取消」结束，避免阻塞 Join 与并发写终端。
                    SkipPrompt(prompt);
                }
                else
                {
                    RunPrompt(prompt);
                }
            }
        }
    }

    /// <summary>停机时跳过尚未进入的提示：以「已取消」完成并释放取消源。</summary>
    private void SkipPrompt(PromptCommand command)
    {
        command.Completion.TrySetCanceled(command.Cancellation.Token);
        command.Cancellation.Dispose();
        _activePrompt = null;
    }

    private void RunPrompt(PromptCommand command)
    {
        _activePrompt = command;

        // 提示期间按 Esc：SelectionPrompt 走 AddCancelResult；TextPrompt 忽略 Esc，
        // 故统一把 Esc 信号链入提示取消源，让所有提示都能被 Esc 取消而不是永久阻塞。
        using var escapeScope = EscapeCancellationScope.Attach(_promptInput, command.Cancellation);
        try
        {
            // 提示令牌即上一次投递的取消源：Esc / 停机都会取消它，底层提示据此收敛，
            // WaitAsync 仅作为「提示不响应令牌」时的兜底，不承担正常取消路径。
            var value = command.Run(_console, command.Cancellation.Token).WaitAsync(command.Cancellation.Token).GetAwaiter().GetResult();
            command.Completion.TrySetResult(value);
        }
        catch (OperationCanceledException)
        {
            // 停止或引擎令牌取消：以「已取消」结束提示，调用方按取消处理。
            command.Completion.TrySetCanceled(command.Cancellation.Token);
        }
        catch (Exception ex)
        {
            command.Completion.TrySetException(ex);
        }
        finally
        {
            _activePrompt = null;
            command.Cancellation.Dispose();
        }
    }

    /// <summary>
    /// 是否跳过在途提示：渲染线程进入提示前判定，已请求停止则不进入。纯函数，供单测（不启动渲染线程）。
    /// </summary>
    internal static bool ShouldSkipPrompt(bool stopRequested) => stopRequested;

    /// <summary>
    /// 终端恢复幂等闸：首次调用返回 <c>true</c>（需执行恢复），后续返回 <c>false</c>（已执行/正在执行）。
    /// 纯函数，供单测。
    /// </summary>
    internal static bool TryMarkRestored(ref int restoredFlag) => Interlocked.Exchange(ref restoredFlag, 1) == 0;

    /// <summary>
    /// 恢复终端呈现状态。<b>幂等</b>：StopAsync 正常路径与 Join 超时兜底路径都可能调用，仅首次生效。
    /// <para>
    /// 分工：<c>\x1b[?2004l</c>（关闭 bracketed paste）由 <see cref="RawInputReader"/> 在停止时写出；
    /// 此处只负责恢复光标可见 <c>\x1b[?25h</c>，同一序列不重复两处写出。
    /// </para>
    /// </summary>
    private void RestoreTerminal()
    {
        if (!TryMarkRestored(ref _terminalRestored))
            return;

        try
        {
            var writer = _console.Profile.Out.Writer;
            writer.Write(TerminalRestoreSequence);
            writer.Flush();
        }
        catch
        {
            // 恢复失败无需中断退出流程。
        }
    }

    private abstract record RenderCommand;

    private sealed record UpdateCommand(IRenderable View) : RenderCommand;

    private sealed record CommitCommand(IRenderable Renderable) : RenderCommand;

    private sealed record PromptCommand(
        Func<IAnsiConsole, CancellationToken, Task<object?>> Run,
        TaskCompletionSource<object?> Completion,
        CancellationTokenSource Cancellation) : RenderCommand;

    private sealed record StopCommand : RenderCommand;
}

/// <summary>
/// <see cref="IAnsiConsole"/> 装饰器：除 <see cref="Input"/> 外全部委托内层控制台，
/// 使 Spectre 提示从注入的通道输入读取，而输出/Live 仍走全局控制台。
/// </summary>
internal sealed class InputOverrideConsole : IAnsiConsole
{
    private readonly IAnsiConsole _inner;
    private readonly IAnsiConsoleInput _input;

    public InputOverrideConsole(IAnsiConsole inner, IAnsiConsoleInput input)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public Profile Profile => _inner.Profile;

    public IAnsiConsoleCursor Cursor => _inner.Cursor;

    public IAnsiConsoleInput Input => _input;

    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;

    public RenderPipeline Pipeline => _inner.Pipeline;

    public void Clear(bool home) => _inner.Clear(home);

    public void Write(IRenderable renderable) => _inner.Write(renderable);

    public void WriteAnsi(Action<AnsiWriter> action) => _inner.WriteAnsi(action);
}
