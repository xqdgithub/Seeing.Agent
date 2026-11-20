using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Execution;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.Shell;

/// <summary>
/// <c>kind=process</c> 的后台活动启动器——把一条 shell 命令变成后台活动（如 <c>bash background=true</c>）。
/// <para>
/// <b>入参约定</b>（<see cref="BackgroundActivityStartRequest.Payload"/>，字段名由本 launcher 定义）：
/// </para>
/// <list type="table">
///   <item>
///     <term><c>command</c>（string，必填）</term>
///     <description>要执行的命令原文，由 <see cref="IShellService"/> 按当前平台 Shell 包装。</description>
///   </item>
///   <item>
///     <term><c>workdir</c>（string，可选）</term>
///     <description>工作目录；缺省依次回落到 <see cref="BackgroundActivityLaunchContext.WorkingDirectory"/>
///     与 <see cref="BackgroundActivityStartRequest.WorkingDirectory"/>。路径合法性由发起方（工具）在转后台前判定。</description>
///   </item>
///   <item>
///     <term><c>timeoutMs</c>（number，可选）</term>
///     <description>子进程侧超时（毫秒），注入 <see cref="SubprocessSpec.Timeout"/>&#160;并作为 launcher 自身的兜底看门狗；
///     活动级超时的权威仍是 manager 的 <see cref="BackgroundActivityStartRequest.Timeout"/>（触发时由 manager 落 <c>Cancelled</c>）。</description>
///   </item>
/// </list>
/// <para>
/// <b>取消语义</b>：执行期只观察 <see cref="BackgroundActivityHandle.CancellationToken"/>，
/// 不链接 <c>ToolContext.CancellationToken</c>，也不自建活动句柄/CTS 之外的终态来源。
/// handle 被取消时（manager 已先落 <c>Cancelled</c>）只等进程自然结束并直接返回，不重复上报终态。
/// </para>
/// <para>
/// <b>进程生命周期</b>：<see cref="SubprocessLifecycle.HostBound"/>——宿主退出（含崩溃）时由内核回收整棵进程树。
/// </para>
/// <para>
/// <b>心跳</b>：运行期每 <see cref="DefaultHeartbeatInterval"/> 调一次
/// <see cref="IBackgroundActivityReporter.Heartbeat"/>，进程退出即停；由注入的 <see cref="TimeProvider"/> 驱动。
/// </para>
/// <para>
/// <b>等待上限</b>：等执行任务收尾（<see cref="CancelAsync"/>）与等进程退出收尾（<see cref="RunAsync"/>）
/// 都受 <see cref="DefaultExitWaitTimeout"/> 约束——<c>Kill</c> 失败时不得把工作区切换/模块停用永久挂住。
/// </para>
/// </summary>
public sealed class ProcessBackgroundActivityLauncher : IBackgroundActivityLauncher
{
    /// <summary>本 launcher 处理的 kind。</summary>
    public const string ProcessKind = "process";

    /// <summary>启动失败的错误码。</summary>
    public const string LaunchFailedErrorCode = "launch_failed";

    /// <summary>payload 指定的子进程超时到期时的错误码。</summary>
    public const string TimeoutErrorCode = "timeout";

    /// <summary><see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 可接受的最大延时（约 49.7 天）。</summary>
    private static readonly TimeSpan s_maxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>等执行任务 / 等进程退出的默认上限：超过即放弃等待，绝不永久挂住调用方。</summary>
    public static readonly TimeSpan DefaultExitWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>运行期心跳的默认间隔（契约要求至少每 30 秒一次）。</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IExecutionWorld _world;
    private readonly IShellService _shellService;
    private readonly IShellEnvironmentService _shellEnvironment;
    private readonly ILogger<ProcessBackgroundActivityLauncher> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _exitWaitTimeout;
    private readonly TimeSpan _heartbeatInterval;

    /// <summary>activityId → 在途进程（取消时按句柄找到进程与执行任务）。</summary>
    private readonly ConcurrentDictionary<string, ProcessEntry> _running = new(StringComparer.Ordinal);

    /// <param name="timeProvider">心跳与等待上限的时钟；缺省 <see cref="TimeProvider.System"/>。</param>
    /// <param name="exitWaitTimeout">等执行任务 / 等进程退出的上限；缺省 <see cref="DefaultExitWaitTimeout"/>。</param>
    /// <param name="heartbeatInterval">运行期心跳间隔；缺省 <see cref="DefaultHeartbeatInterval"/>。</param>
    public ProcessBackgroundActivityLauncher(
        IExecutionWorld world,
        IShellService shellService,
        IShellEnvironmentService shellEnvironment,
        ILogger<ProcessBackgroundActivityLauncher> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? exitWaitTimeout = null,
        TimeSpan? heartbeatInterval = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _shellService = shellService ?? throw new ArgumentNullException(nameof(shellService));
        _shellEnvironment = shellEnvironment ?? throw new ArgumentNullException(nameof(shellEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _exitWaitTimeout = exitWaitTimeout is { } wait && wait > TimeSpan.Zero ? wait : DefaultExitWaitTimeout;
        _heartbeatInterval = heartbeatInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultHeartbeatInterval;
    }

    /// <inheritdoc />
    public string Kind => ProcessKind;

    /// <inheritdoc />
    public BackgroundActivityCapabilities Capabilities =>
        BackgroundActivityCapabilities.CanCancel
        | BackgroundActivityCapabilities.CanWait
        | BackgroundActivityCapabilities.CanStreamOutput
        | BackgroundActivityCapabilities.SignalsCompletion;

    /// <inheritdoc />
    public async Task<BackgroundActivityLaunchResult> LaunchAsync(
        BackgroundActivityLaunchContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var handle = ctx.Handle;

        if (!TryGetCommand(ctx.Request.Payload, out var command, out var parseError))
        {
            await ReportLaunchFailureAsync(handle, parseError).ConfigureAwait(false);
            return new BackgroundActivityLaunchResult(Task.CompletedTask);
        }

        var workdir = GetString(ctx.Request.Payload, "workdir")
            ?? ctx.WorkingDirectory
            ?? ctx.Request.WorkingDirectory;

        if (string.IsNullOrWhiteSpace(workdir))
        {
            await ReportLaunchFailureAsync(handle, "payload 缺少 command 或未提供工作目录。").ConfigureAwait(false);
            return new BackgroundActivityLaunchResult(Task.CompletedTask);
        }

        var timeout = GetTimeout(ctx.Request.Payload, out var timeoutError);
        if (timeoutError is not null)
        {
            await ReportLaunchFailureAsync(handle, timeoutError).ConfigureAwait(false);
            return new BackgroundActivityLaunchResult(Task.CompletedTask);
        }

        ISubprocess process;
        try
        {
            var shell = _shellService.SelectShell();
            var environment = await _shellEnvironment
                .GetEnvironmentAsync(workdir, ctx.Request.OwnerSessionId, ctx.Request.OriginToolCallId, ct)
                .ConfigureAwait(false);
            var prepared = _shellService.PrepareCommand(shell, command);
            var argumentList = _shellService.BuildArgumentList(shell, prepared);

            var spec = new SubprocessSpec
            {
                FileName = shell,
                // POSIX：argv 直传，避免 Linux 把 -c '…' 按空白拆开。cmd/powershell 仍走原始命令行。
                Arguments = argumentList is null ? _shellService.BuildArguments(shell, prepared) : "",
                ArgumentList = argumentList,
                WorkingDirectory = workdir,
                Environment = EnsureUtf8OutputEnvironment(environment),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Encoding = Encoding.UTF8,
                // 宿主退出即回收整棵进程树（spec §16）。
                Lifecycle = SubprocessLifecycle.HostBound,
                Timeout = timeout,
            };

            process = _world.Subprocess.Start(spec);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 启动期被取消：终态由 manager 落 Cancelled，launcher 不抢。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "后台进程启动失败 ActivityId={ActivityId} Command={Command}",
                handle.ActivityId,
                command);

            await ReportLaunchFailureAsync(handle, ex.Message).ConfigureAwait(false);
            return new BackgroundActivityLaunchResult(Task.CompletedTask);
        }

        var entry = new ProcessEntry(process, timeout);
        // 先登记再启动执行任务：执行可能同步完成并把自己摘掉。
        _running[handle.ActivityId] = entry;

        var execution = RunAsync(entry, ctx);
        entry.Execution = execution;

        // 启动成功立即返回：执行任务由 manager 观察（终态由 reporter 上报）。
        return new BackgroundActivityLaunchResult(execution);
    }

    /// <inheritdoc />
    public async Task CancelAsync(BackgroundActivityHandle handle, BackgroundActivityCancelReason reason)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!_running.TryGetValue(handle.ActivityId, out var entry))
            return;

        try
        {
            entry.Process.Kill(entireTree: true);
        }
        catch (Exception ex)
        {
            // 进程可能已自行退出：杀失败不影响终态（manager 已落 Cancelled）。
            _logger.LogDebug(
                ex,
                "后台进程终止失败（可能已退出） ActivityId={ActivityId} Reason={Reason}",
                handle.ActivityId,
                reason);
        }

        // 等执行任务自然结束：模块停用/宿主关闭不得留下仍在跑的收尾逻辑。
        // 但必须设上限——Kill 失败时执行任务可能永远等不到进程退出，无上限会把工作区切换/停用挂死。
        if (entry.Execution is { } execution)
        {
            try
            {
                await execution
                    .WaitAsync(_exitWaitTimeout, _timeProvider, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "取消后台进程时等待执行任务收尾超时，放弃等待 ActivityId={ActivityId} Reason={Reason} TimeoutMs={TimeoutMs}",
                    handle.ActivityId,
                    reason,
                    _exitWaitTimeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "后台进程执行任务在取消后以异常结束 ActivityId={ActivityId}", handle.ActivityId);
            }
        }
    }

    /// <summary>
    /// 执行面：泵输出 → 等退出 → 上报终态；运行期按 <see cref="DefaultHeartbeatInterval"/> 上报心跳。
    /// </summary>
    private async Task RunAsync(ProcessEntry entry, BackgroundActivityLaunchContext ctx)
    {
        var handle = ctx.Handle;
        var cancellation = handle.CancellationToken;

        CancellationTokenSource? heartbeatCts = null;
        Task? heartbeatLoop = null;

        try
        {
            // 取消可能早于本 launcher 登记进程（会话取消与启动并发）：此时 manager 已落终态，
            // CancelAsync 也找不到条目，必须在这里自行回收，避免留下无人管的进程。
            if (cancellation.IsCancellationRequested)
            {
                KillQuietly(entry.Process, handle.ActivityId);
                await WaitForExitQuietlyAsync(entry.Process, handle.ActivityId).ConfigureAwait(false);
                return;
            }

            using var timeoutCts = new CancellationTokenSource();
            if (entry.Timeout is { } timeout && timeout > TimeSpan.Zero)
                timeoutCts.CancelAfter(timeout > s_maxTimerDelay ? s_maxTimerDelay : timeout);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCts.Token);
            var waitToken = waitCts.Token;

            // 运行期心跳：契约要求至少每 30 秒一次，否则 UI 会把长任务标成「疑似卡死」。
            heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(waitToken);
            heartbeatLoop = RunHeartbeatLoopAsync(entry, handle, heartbeatCts.Token);

            var stdout = PumpAsync(entry, handle, entry.Process.StandardOutput, waitToken);
            var stderr = PumpAsync(entry, handle, entry.Process.StandardError, waitToken);

            var timedOut = false;
            try
            {
                await entry.Process.WaitForExitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // handle 取消由 manager 裁决（且 CancelAsync 负责 Kill）；只有超时在此自解。
                timedOut = !cancellation.IsCancellationRequested;
                if (timedOut)
                    KillQuietly(entry.Process, handle.ActivityId);

                await WaitForExitQuietlyAsync(entry.Process, handle.ActivityId).ConfigureAwait(false);
            }

            // 进程已退出（含被杀）：先停心跳再做收尾，别把已退出的进程继续标成「活着」。
            await StopHeartbeatLoopAsync(heartbeatCts, heartbeatLoop).ConfigureAwait(false);
            heartbeatCts.Dispose();
            heartbeatCts = null;
            heartbeatLoop = null;

            await DrainAsync(stdout, stderr).ConfigureAwait(false);

            if (cancellation.IsCancellationRequested)
                return; // manager 已落 Cancelled：不重复上报终态。

            if (timedOut)
            {
                await handle.Reporter
                    .FailAsync(TimeoutErrorCode, $"命令超过 {entry.Timeout?.TotalMilliseconds:0} 毫秒后被终止。", CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var exitCode = entry.Process.ExitCode;
            await handle.Reporter
                .CompleteAsync(
                    new BackgroundActivityResult(
                        Kind: BackgroundActivityResultKind.ProcessExit,
                        ArtifactPath: null,
                        ResultSessionId: null,
                        ExitCode: exitCode,
                        ErrorCode: null,
                        ErrorMessage: null),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 未上报终态 → manager 记 execution_faulted；这里只留诊断。
            _logger.LogError(ex, "后台进程执行异常 ActivityId={ActivityId}", handle.ActivityId);
            throw;
        }
        finally
        {
            await StopHeartbeatLoopAsync(heartbeatCts, heartbeatLoop).ConfigureAwait(false);
            heartbeatCts?.Dispose();
            _running.TryRemove(handle.ActivityId, out _);
            entry.Dispose();
        }
    }

    /// <summary>
    /// 运行期心跳：每 <see cref="DefaultHeartbeatInterval"/> 报一次，取消（进程退出 / 活动取消 / 超时）即停。
    /// 心跳失败只丢这一次上报，绝不影响进程与终态。
    /// </summary>
    private async Task RunHeartbeatLoopAsync(
        ProcessEntry entry,
        BackgroundActivityHandle handle,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, _timeProvider, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                    return;

                try
                {
                    handle.Reporter.Heartbeat();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "上报后台进程心跳失败 ActivityId={ActivityId}", handle.ActivityId);
                }

                // 进程退出后不再心跳：终态上报不认心跳，继续打点会让已结束的活动看起来还在跑。
                if (HasExitedQuietly(entry.Process))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // 进程退出 / 活动取消：心跳到此结束。
        }
    }

    /// <summary>停心跳并等它退出；只做收尾，失败不影响终态裁决。</summary>
    private static async Task StopHeartbeatLoopAsync(CancellationTokenSource? cts, Task? loop)
    {
        if (cts is null)
            return;

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // CTS 已释放：心跳早已结束。
        }

        if (loop is null)
            return;

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 心跳循环自身的异常不该冒到执行面。
        }
    }

    private static bool HasExitedQuietly(ISubprocess process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>逐行读取一条流并追加到活动输出；两个泵共用 <see cref="ProcessEntry.AppendGate"/> 串行化。</summary>
    private static async Task PumpAsync(
        ProcessEntry entry,
        BackgroundActivityHandle handle,
        TextReader reader,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                    break;

                var chunk = Encoding.UTF8.GetBytes(AnsiEscape.Strip(line) + "\n");
                await entry.AppendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await handle.Reporter.AppendOutputAsync(chunk, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    entry.AppendGate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读流失败只丢这一路输出，不影响进程与终态。
        }
    }

    private static async Task DrainAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 收尾等待进程退出，受退出等待上限（默认 <see cref="DefaultExitWaitTimeout"/>）约束：<c>Kill</c> 失败时
    /// 宁可放弃等待（记 Warning），也不能把执行任务（进而把取消/停用/工作区切换）永久挂住。
    /// </summary>
    private async Task WaitForExitQuietlyAsync(ISubprocess process, string activityId)
    {
        try
        {
            await process
                .WaitForExitAsync(CancellationToken.None)
                .WaitAsync(_exitWaitTimeout, _timeProvider, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "等待后台进程退出超时，放弃收尾等待 ActivityId={ActivityId} TimeoutMs={TimeoutMs}",
                activityId,
                _exitWaitTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // 只为把进程收尾干净；失败不影响终态裁决。
            _logger.LogDebug(ex, "等待后台进程退出失败 ActivityId={ActivityId}", activityId);
        }
    }

    private void KillQuietly(ISubprocess process, string activityId)
    {
        try
        {
            process.Kill(entireTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "后台进程超时终止失败 ActivityId={ActivityId}", activityId);
        }
    }

    private async Task ReportLaunchFailureAsync(BackgroundActivityHandle handle, string message)
    {
        _logger.LogWarning("后台进程启动失败 ActivityId={ActivityId} Reason={Reason}", handle.ActivityId, message);

        try
        {
            await handle.Reporter.FailAsync(LaunchFailedErrorCode, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "上报启动失败终态时出错 ActivityId={ActivityId}", handle.ActivityId);
        }
    }

    // ─── payload 解析 ────────────────────────────────────────────────────────

    private static bool TryGetCommand(JsonElement? payload, out string command, out string error)
    {
        command = GetString(payload, "command") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "payload 缺少非空 command 字段。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string? GetString(JsonElement? payload, string name)
    {
        if (payload is not { } element || element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TimeSpan? GetTimeout(JsonElement? payload, out string? error)
    {
        error = null;
        if (payload is not { } element || element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty("timeoutMs", out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var ms) || ms <= 0
            || !double.IsFinite(ms) || ms > TimeSpan.MaxValue.TotalMilliseconds)
        {
            error = "payload 的 timeoutMs 必须是正数（毫秒）。";
            return null;
        }

        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// 与 <c>BashTool</c> 一致的 UTF-8 输出默认值：不让子进程按系统代码页写重定向输出。
    /// </summary>
    private static Dictionary<string, string?> EnsureUtf8OutputEnvironment(
        IReadOnlyDictionary<string, string> environment)
    {
        var env = environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        env.TryAdd("PYTHONIOENCODING", "utf-8");
        env.TryAdd("PYTHONUTF8", "1");
        env.TryAdd("LANG", "C.UTF-8");
        env.TryAdd("LC_ALL", "C.UTF-8");
        env.TryAdd("NO_COLOR", "1");
        return env;
    }

    /// <summary>在途进程条目；<see cref="Execution"/> 由 <see cref="LaunchAsync"/> 回填（取消可能先于回填）。</summary>
    private sealed class ProcessEntry : IDisposable
    {
        public ProcessEntry(ISubprocess process, TimeSpan? timeout)
        {
            Process = process;
            Timeout = timeout;
        }

        public ISubprocess Process { get; }

        public TimeSpan? Timeout { get; }

        /// <summary>stdout/stderr 两个泵的追加互斥（reporter 不承诺并发安全）。</summary>
        public SemaphoreSlim AppendGate { get; } = new(1, 1);

        private volatile Task? _execution;

        /// <summary>执行任务；<see cref="LaunchAsync"/> 回填，取消通知可能先于回填读到 <see langword="null"/>。</summary>
        public Task? Execution
        {
            get => _execution;
            set => _execution = value;
        }

        public void Dispose()
        {
            try
            {
                Process.Dispose();
            }
            catch (Exception)
            {
                // 进程句柄释放失败不影响活动终态。
            }

            AppendGate.Dispose();
        }
    }
}
