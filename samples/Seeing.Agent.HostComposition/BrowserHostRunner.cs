using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using Seeing.Agent.Tools.Browser;

namespace Seeing.Agent.HostComposition;

/// <summary>
/// <c>--browser-host</c> / <c>--watchdog</c> 子命令的运行器。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不新增独立可执行文件</b>：宿主必须与客户端<b>同版本</b>（协议、发现文件、Screencast 都在改）。
/// 让每个既有入口（Desktop / WebUI / TUI / CLI）在 <c>Main</c> 开头识别这两个开关，
/// 客户端就能用「当前进程的可执行文件」拉起宿主，天然不会版本错配。
/// </para>
/// <para>
/// <b>为什么放在 samples 层</b>：它要直接 new <see cref="BrowserProcessHost"/> 并编排
/// profile 锁、发现文件与日志，属于宿主组合根的职责；能力包只依赖 Abstractions + 原语。
/// </para>
/// <para>
/// <b>进程生死的分工</b>：Windows 靠「宿先把<b>自己</b> assign 进 Job」——宿主被任务管理器强杀时，
/// 内核关掉 job 句柄即连带杀掉浏览器；POSIX 没有等价内核对象，改为 spawn 一个
/// <c>--watchdog &lt;hostPid&gt; &lt;browserPid&gt;</c> 子进程轮询宿主存活。
/// </para>
/// </remarks>
public static class BrowserHostRunner
{
    /// <summary>宿主模式开关（与 <see cref="WatchdogSwitch"/> 一起在 <c>Main</c> 开头分发）。</summary>
    public const string BrowserHostSwitch = "--browser-host";

    /// <summary>看门狗模式开关。</summary>
    public const string WatchdogSwitch = "--watchdog";

    /// <summary>默认空闲退出秒数（无存活客户端持续这么久才退出）。</summary>
    public const int DefaultHostIdleSeconds = 1800;

    /// <summary>旧版本（客户端直连模式）留下的 PID 文件名。</summary>
    private const string LegacyPidFileName = ".seeing-engine.pid";

    /// <summary>主循环的节拍（空闲判定与客户端扫描的精度）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>回收死客户端遗留页面的周期。</summary>
    private static readonly TimeSpan ReclaimInterval = TimeSpan.FromSeconds(30);

    /// <summary>profile 锁恢复的重试上限。</summary>
    private static readonly TimeSpan ProfileLockRetryLimit = TimeSpan.FromSeconds(15);

    /// <summary>profile 锁恢复的重试间隔（等 Chromium 释放 Singleton 锁）。</summary>
    private static readonly TimeSpan ProfileLockRetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>看门狗轮询宿主存活的间隔。</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

    /// <summary>看门狗的宽限期：宿主刚启动/退出时给一次容错，避免误判。</summary>
    private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 运行 <c>--browser-host</c> 或 <c>--watchdog</c> 模式。
    /// </summary>
    /// <param name="args">完整命令行参数（含模式开关）。</param>
    /// <param name="ct">取消令牌（宿主收到停止信号时传入）。</param>
    /// <returns>进程退出码：0 = 正常（含「已有宿主」与空闲退出），1 = 浏览器不可用 / 崩溃，2 = 参数错误。</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains(WatchdogSwitch, StringComparer.Ordinal))
            return await RunWatchdogAsync(args, ct).ConfigureAwait(false);

        try
        {
            return await RunHostAsync(args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止信号：宿主已经在 finally 里关掉浏览器并删掉发现文件，正常退出。
            return 0;
        }
    }

    /// <summary>
    /// 宿主自持有的 Job 句柄：<b>进程级常驻，绝不显式 Dispose</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能 <c>using</c> / <c>Dispose</c></b>：Job 带
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>，句柄一关内核立即终止 job 内所有进程 ——
    /// 而宿主自己就在这个 job 里。方法返回或异常展开时关句柄，等于在<b>提交退出码之前</b>
    /// 把自己杀掉：所有 <c>return 1</c> 都变成 exit 0，崩溃也不报错，日志尾部直接消失。
    /// </para>
    /// <para>
    /// <b>正确的关闭时机</b>：进程真正终止时由内核统一关闭句柄 ——
    /// 那一刻浏览器（如果还活着）被连带清掉，正是我们要的语义。
    /// 因此这里只保留一个静态引用，全生命周期不解引用、不 Dispose。
    /// </para>
    /// </remarks>
    private static BrowserJobObject? s_hostJob;

    /// <summary>
    /// 确保宿主进程已放入 Job（仅 Windows；失败只记 Warning，不阻止启动）。
    /// </summary>
    private static void EnsureHostJob(ILogger log)
    {
        if (!OperatingSystem.IsWindows() || Volatile.Read(ref s_hostJob) is not null)
            return;

        s_hostJob = BrowserJobObject.AssignCurrentProcess();
        if (s_hostJob is null)
            log.LogWarning("无法把宿主进程放进 Job；宿主被强杀时浏览器可能残留");
    }

    #region 看门狗

    /// <summary><c>--watchdog &lt;hostPid&gt; &lt;browserPid&gt;</c>：宿主消失则杀掉浏览器整树。</summary>
    private static async Task<int> RunWatchdogAsync(string[] args, CancellationToken ct)
    {
        var index = Array.IndexOf(args, WatchdogSwitch);
        if (index < 0 || args.Length < index + 3)
        {
            WriteError("--watchdog 需要两个参数：宿主 PID 与浏览器 PID。");
            return 2;
        }

        if (!int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPid)
            || !int.TryParse(args[index + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var browserPid)
            || hostPid <= 0
            || browserPid <= 0)
        {
            WriteError("--watchdog 的宿主 PID 与浏览器 PID 必须是正整数。");
            return 2;
        }

        // 宽限期：宿主刚 spawn 浏览器、命令行可能还没被系统完全登记；立刻判定会把正常启动误杀。
        try
        {
            await Task.Delay(WatchdogGrace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        while (!ct.IsCancellationRequested)
        {
            if (!IsProcessAlive(hostPid))
            {
                KillProcessTree(browserPid);
                return 0;
            }

            if (!IsProcessAlive(browserPid))
            {
                // 浏览器先退出：宿主会自行发现并退出，看门狗无事可做。
                return 0;
            }

            try
            {
                await Task.Delay(WatchdogInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        return 0;
    }

    #endregion

    #region 宿主

    private static async Task<int> RunHostAsync(string[] args, CancellationToken ct)
    {
        var parsed = ParseHostStartup(args);
        if (parsed.Options is not { } startup)
        {
            WriteError(parsed.Error ?? UsageError);
            return 2;
        }

        var profile = BrowserHostLocator.NormalizeProfileDirectory(
            BrowserHostLocator.ResolveHostProfileDirectory(startup.ProfileDirectory));

        try
        {
            Directory.CreateDirectory(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteError($"浏览器 profile 目录不可用：{profile}（{ex.Message}）");
            return 1;
        }

        var locator = new BrowserHostLocator(profile);
        try
        {
            Directory.CreateDirectory(locator.BaseDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteError($"浏览器宿主目录不可用：{locator.BaseDirectory}（{ex.Message}）");
            return 1;
        }

        using var log = new BrowserHostLog(BrowserHostLog.ResolvePath(locator.BaseDirectory, locator.ProfileHash));

        // 单例：拿不到锁说明已有宿主在跑。这是正常竞争，安静退出 0 ——
        // 用户界面不该因为「浏览器已经开着了」弹出红色错误。
        using var hostLock = BrowserHostLock.TryAcquire(profile);
        if (hostLock is null)
        {
            log.LogInformation("profile {Profile} 已有宿主，本次启动退出", profile);
            return 0;
        }

        var generation = Guid.NewGuid().ToString("N")[..8];
        var hostStartedAt = ResolveProcessStartedAt(Environment.ProcessId);
        var options = CreateBrowserOptions(startup, profile);
        var resolvedExecutable = BrowserEngine.ResolveExecutable(options);
        var effective = new BrowserHostEffective(
            options.Headless,
            resolvedExecutable.Path,
            options.UserAgent,
            options.AntiDetection,
            options.ExtraArgs ?? [],
            startup.IdleSeconds);

        log.LogInformation(
            "浏览器宿主启动（pid={Pid}，generation={Generation}，profile={Profile}，" +
            "headless={Headless}，antiDetection={AntiDetection}）",
            Environment.ProcessId,
            generation,
            profile,
            effective.Headless,
            effective.AntiDetection);

        // 发现文件的两个状态位：
        //   published —— 写过（成功或失败信息）；
        //   keepInfo  —— 退出时保留（失败/崩溃的 lastError 是排查线索，删掉就再也看不到）。
        var published = false;
        var keepInfo = false;

        bool Publish(string endpoint, int? browserPid, DateTimeOffset? browserStartedAt, string? lastError)
        {
            try
            {
                locator.Write(new BrowserHostInfo(
                    BrowserHostLocator.CurrentVersion,
                    generation,
                    Environment.ProcessId,
                    hostStartedAt,
                    profile,
                    browserPid,
                    browserStartedAt,
                    endpoint,
                    lastError,
                    effective));
                published = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError(ex, "写发现文件失败：{Path}", locator.InfoPath);
                return false;
            }
        }

        // 旧版本迁移：旧直连模式会留 .seeing-engine.pid 与可能仍占着 profile 的浏览器进程。
        var migrationError = TryClearLegacyInstance(profile, log);
        if (migrationError is not null)
        {
            log.LogError("{Error}", migrationError);
            Publish(string.Empty, null, null, migrationError);
            keepInfo = true;
            WriteError(migrationError);
            return 1;
        }

        // Windows：先把自己放进 Job，再 spawn Chromium —— 只有之后的子进程才会自动入 job。
        // 句柄故意常驻到进程终结（见 s_hostJob 注释）：提前 Dispose 会把宿主自己杀掉。
        EnsureHostJob(log);

        var launched = await LaunchAsync(options, profile, log, ct).ConfigureAwait(false);
        if (launched.Host is not { } processHost)
        {
            var error = launched.Error ?? "浏览器启动失败";
            log.LogError("浏览器启动失败：{Error}", error);
            Publish(string.Empty, null, null, error);
            keepInfo = true;
            WriteError(error);
            return 1;
        }

        Process? watchdog = null;
        try
        {
            var browserPid = processHost.BrowserPid > 0 ? processHost.BrowserPid : (int?)null;
            var browserStartedAt = browserPid is null ? (DateTimeOffset?)null : processHost.BrowserStartedAt;

            if (!Publish(processHost.Endpoint, browserPid, browserStartedAt, null))
            {
                // 客户端找不到宿主就等于宿主没起来；继续跑只会占着浏览器不放。
                return 1;
            }

            log.LogInformation(
                "浏览器宿主就绪（browserPid={BrowserPid}，endpoint={Endpoint}，idleSeconds={IdleSeconds}）",
                browserPid ?? 0,
                processHost.Endpoint,
                startup.IdleSeconds);

            // 客户端拿自己的 BrowserOptions 与 effective 比较，不一致会提示「需重启浏览器」。
            // 宿主侧日志必须留下同一份事实，否则用户只看到提示、看不到到底哪一项不同。
            log.LogInformation(
                "effective：headless={Headless}，executable={Executable}，userAgent={UserAgent}，" +
                "antiDetection={AntiDetection}，extraArgs=[{ExtraArgs}]，hostIdleSeconds={IdleSeconds}",
                effective.Headless,
                effective.ExecutablePath ?? "(auto)",
                effective.UserAgent ?? "(auto)",
                effective.AntiDetection,
                string.Join(" ", effective.ExtraArgs),
                effective.HostIdleSeconds);

            if (!OperatingSystem.IsWindows())
                watchdog = TryStartWatchdog(Environment.ProcessId, processHost.BrowserPid, log);

            return await RunHostLoopAsync(
                processHost,
                locator,
                log,
                startup,
                browserPid,
                browserStartedAt,
                Publish,
                onKeepInfo: () => keepInfo = true,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            await processHost.DisposeAsync().ConfigureAwait(false);

            // 看门狗是子进程：宿主正常退出时它没有存在意义，显式收掉避免残留
            // （异常退出时它自己会发现宿主消失，杀掉浏览器后退出）。
            if (watchdog is not null)
            {
                try
                {
                    if (!watchdog.HasExited)
                        watchdog.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 已经自行退出
                }

                watchdog.Dispose();
            }

            log.LogInformation("浏览器宿主已停止（pid={Pid}）", Environment.ProcessId);

            // 只删「自己的」发现文件（TryDelete 内部校验 hostPid）；
            // 失败/崩溃留下的 lastError 保留，供设置页与人工排查。
            if (published && !keepInfo)
                locator.TryDelete();
        }
    }

    /// <summary>宿主主循环：断连监视、死客户端页面回收、空闲退出。</summary>
    private static async Task<int> RunHostLoopAsync(
        BrowserProcessHost processHost,
        BrowserHostLocator locator,
        ILogger log,
        HostStartup startup,
        int? browserPid,
        DateTimeOffset? browserStartedAt,
        Func<string, int?, DateTimeOffset?, string?, bool> publish,
        Action onKeepInfo,
        CancellationToken ct)
    {
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDisconnected(object? sender, EventArgs e)
            => disconnected.TrySetResult("浏览器进程已断开（崩溃或被外部关闭）");

        processHost.Browser.Disconnected += OnDisconnected;

        // 空闲判定必须是「持续」而不是单次采样：客户端可能刚好在两次扫描之间开页，
        // 单次采样会把一个正在被使用的宿主立刻判死。firstIdleAt 一旦看到客户端就清空。
        DateTimeOffset? firstIdleAt = null;
        var lastReclaimAt = DateTimeOffset.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 连接可能在「发布发现文件」与「订阅事件」之间就断了：
                // 事件已经错过，只能靠状态位补一次判定，否则循环会对着死浏览器空转到空闲超时。
                if (processHost.Browser.IsClosed)
                    disconnected.TrySetResult("浏览器进程已断开（崩溃或被外部关闭）");

                var done = await Task.WhenAny(Task.Delay(TickInterval, ct), disconnected.Task).ConfigureAwait(false);

                if (done == disconnected.Task)
                {
                    var error = await disconnected.Task.ConfigureAwait(false);
                    log.LogError("{Error}", error);

                    // 断连后 endpoint 已经不可用：必须把它从发现文件里抹掉，
                    // 否则客户端会拿一个连不上的地址反复重试。
                    publish(string.Empty, null, null, error);
                    onKeepInfo();
                    return 1;
                }

                if (ct.IsCancellationRequested)
                    break;

                var now = DateTimeOffset.UtcNow;

                if (now - lastReclaimAt >= ReclaimInterval)
                {
                    lastReclaimAt = now;
                    await ReclaimDeadClientsAsync(processHost, locator, log, ct).ConfigureAwait(false);
                }

                var aliveClients = BrowserClientPresence.AliveClientPids(locator.ProfileDirectory);
                if (aliveClients.Count == 0)
                {
                    firstIdleAt ??= now;

                    if (startup.IdleSeconds > 0
                        && now - firstIdleAt.Value >= TimeSpan.FromSeconds(startup.IdleSeconds))
                    {
                        log.LogInformation(
                            "已连续 {Seconds} 秒无存活客户端，宿主退出（HostIdleSeconds）", startup.IdleSeconds);
                        return 0;
                    }
                }
                else
                {
                    firstIdleAt = null;
                }
            }

            log.LogInformation("收到停止信号，宿主退出（browserPid={BrowserPid}）", browserPid ?? 0);
            return 0;
        }
        finally
        {
            processHost.Browser.Disconnected -= OnDisconnected;
        }
    }

    /// <summary>
    /// 启动浏览器；遇到 profile 锁时按「孤儿清理 → 15 秒内重试」恢复。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能直接把 Puppeteer 的原文透出去</b>：用户看到的是
    /// <c>Failed to launch browser!</c> 加一段 stderr，既不知道是谁占着，也不知道该关哪个窗口。
    /// 这里必须给出「哪个 PID / 父进程还活着」这种可操作的信息。
    /// </remarks>
    private static async Task<(BrowserProcessHost? Host, string? Error)> LaunchAsync(
        BrowserOptions options,
        string profile,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            return (await BrowserProcessHost.StartAsync(options, ct, log, hostAlreadyInJob: true)
                .ConfigureAwait(false), null);
        }
        catch (ProcessException ex) when (IsProfileLockMessage(ex))
        {
            log.LogWarning("profile 已被占用，尝试清理孤儿进程后重试：{Message}", ex.Message);
        }
        catch (BrowserUnavailableException ex)
        {
            return (null, ex.Message);
        }
        catch (ProcessException ex)
        {
            // 未识别的启动失败：原始文案对用户没有价值，细节已经在日志里。
            log.LogError(ex, "启动浏览器失败");
            return (null, "浏览器启动失败（底层未给出可解释原因），详细输出见浏览器宿主日志。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "启动浏览器时出现未预期的异常");
            return (null, $"浏览器启动失败：{ex.Message}");
        }

        var candidates = BrowserProcessProbe.FindByProfileDirectory(profile);
        foreach (var candidate in candidates)
        {
            if (!IsProcessAlive(candidate.Pid))
                continue;

            // 父进程 PID 为 0 表示「读不到」，不是「没有父进程」：
            // 此时宁可不清理并明确拒绝，也不能误杀一个可能正在被用户使用的窗口。
            if (candidate.ParentPid <= 0)
            {
                return (null,
                    $"无法确认占用 profile 的浏览器进程 {candidate.Pid} 的父进程，拒绝自动清理。" +
                    "请手动关闭该进程后重试。");
            }

            if (IsProcessAlive(candidate.ParentPid))
            {
                return (null,
                    $"profile 正被浏览器进程 {candidate.Pid}（父进程 {candidate.ParentPid}）占用。" +
                    "请先关闭它（或结束该进程）后重试。");
            }

            log.LogWarning("清理父进程已退出的孤儿浏览器进程 {Pid}（{Path}）",
                candidate.Pid, candidate.ExecutablePath);
            KillProcessTree(candidate.Pid);
        }

        if (candidates.Count == 0)
        {
            log.LogWarning("未找到命令行匹配 profile 的进程，可能是锁释放有延迟（继续重试）");
        }

        var deadline = DateTimeOffset.UtcNow + ProfileLockRetryLimit;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return (await BrowserProcessHost.StartAsync(options, ct, log, hostAlreadyInJob: true)
                    .ConfigureAwait(false), null);
            }
            catch (ProcessException ex) when (IsProfileLockMessage(ex))
            {
                log.LogDebug("profile 锁尚未释放，继续重试");
            }
            catch (ProcessException ex)
            {
                // 重试阶段也可能出现「换了种说法」的启动失败：同样不能把原文透给用户。
                log.LogError(ex, "启动浏览器失败（重试阶段）");
                return (null, "浏览器启动失败（底层未给出可解释原因），详细输出见浏览器宿主日志。");
            }
            catch (BrowserUnavailableException ex)
            {
                return (null, ex.Message);
            }

            await Task.Delay(ProfileLockRetryInterval, ct).ConfigureAwait(false);
        }
        return (null,
            $"profile {profile} 在 {ProfileLockRetryLimit.TotalSeconds:0} 秒内仍被占用，" +
            "请关闭占用它的浏览器进程后重试。");
    }

    /// <summary>
    /// 回收「锁已释放」的客户端留下的页面与清单。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么按 targetId 关页面而不是按客户端</b>：客户端崩溃时不会执行任何清理代码，
    /// 唯一可靠的事实是它写下的 <c>pages.json</c>。<c>Target.closeTarget</c> 是浏览器级操作，
    /// 不需要客户端连接存在。
    /// </para>
    /// <para>
    /// <b>为什么删除锁文件是安全的</b>：扫描前刚刚判定该 PID 已死（<see cref="BrowserClientPresence.AliveClientPids"/>）。
    /// 若恰好在扫描与删除之间该 PID 被复用并重新持锁，<c>File.Delete</c> 在 Windows 上会因共享冲突失败，
    /// 我们只记 Debug 并跳过 —— 不能把活客户端的锁删掉。
    /// </para>
    /// </remarks>
    private static async Task ReclaimDeadClientsAsync(
        BrowserProcessHost processHost,
        BrowserHostLocator locator,
        ILogger log,
        CancellationToken ct)
    {
        if (!Directory.Exists(locator.ClientsDirectory))
            return;

        IReadOnlyList<int> alivePids;
        try
        {
            alivePids = BrowserClientPresence.AliveClientPids(locator.ProfileDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "扫描存活客户端失败（本轮跳过回收）");
            return;
        }

        var alive = new HashSet<int>(alivePids);
        string[] lockFiles;
        try
        {
            lockFiles = Directory.GetFiles(
                locator.ClientsDirectory, "*" + BrowserClientPresence.LockFileExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "枚举客户端锁失败（本轮跳过回收）");
            return;
        }

        ICDPSession? session = null;
        try
        {
            foreach (var lockFile in lockFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (!int.TryParse(
                        Path.GetFileNameWithoutExtension(lockFile),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var clientPid)
                    || clientPid <= 0
                    || alive.Contains(clientPid))
                {
                    continue;
                }

                var targets = BrowserClientPresence.ReadPageTargets(locator.ProfileDirectory, clientPid);
                if (targets.Count > 0)
                {
                    session ??= await processHost.Browser.Target.CreateCDPSessionAsync().ConfigureAwait(false);

                    foreach (var targetId in targets)
                    {
                        if (string.IsNullOrWhiteSpace(targetId))
                            continue;

                        try
                        {
                            await session.SendAsync("Target.closeTarget", new { targetId })
                                .ConfigureAwait(false);
                            log.LogInformation(
                                "已关闭死客户端 {ClientPid} 的遗留页面 {TargetId}", clientPid, targetId);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // 页面可能已经被用户手动关掉 / target 已失效：继续处理其余 target。
                            log.LogDebug(ex, "关闭遗留页面 {TargetId} 失败", targetId);
                        }
                    }
                }

                DeleteClientFiles(locator.ClientsDirectory, clientPid, log);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "回收死客户端页面时失败（下一轮继续）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 浏览器可能已在回收过程中断开：维护路径失败不该把宿主主循环一起带走。
            log.LogWarning(ex, "回收死客户端页面时出现未预期的异常（下一轮继续）");
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.DetachAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 浏览器可能已经断开：detach 失败不影响宿主主循环。
                    log.LogDebug(ex, "释放 CDP 会话失败");
                }
            }
        }
    }

    private static void DeleteClientFiles(string clientsDirectory, int clientPid, ILogger log)
    {
        var lockPath = Path.Combine(clientsDirectory, clientPid + BrowserClientPresence.LockFileExtension);
        var pagesPath = Path.Combine(clientsDirectory, clientPid + BrowserClientPresence.PagesFileExtension);

        foreach (var path in new[] { pagesPath, lockPath })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 锁仍在被持有（PID 被复用）/ 权限不足：保留文件比误删活客户端的锁安全。
                log.LogDebug(ex, "删除死客户端文件失败：{Path}", path);
            }
        }
    }

    #endregion

    #region 配置与参数

    private sealed record HostStartup(
        string ProfileDirectory,
        int IdleSeconds,
        bool Headless,
        string? ExecutablePath,
        string? UserAgent,
        bool AntiDetection,
        string[] ExtraArgs);

    private sealed record HostStartupParseResult(HostStartup? Options, string? Error);

    private const string UsageError =
        "用法：--browser-host --profile <dir> [--idle-seconds N] [--headless <true|false>] " +
        "[--anti-detection <true|false>] [--executable <path>] [--user-agent <ua>] " +
        "[--extra-arg <arg>]...";

    private static HostStartupParseResult ParseHostStartup(string[] args)
    {
        string? profile = null;
        var idleSeconds = DefaultHostIdleSeconds;
        var headless = true;
        var antiDetection = true;
        string? executablePath = null;
        string? userAgent = null;
        var extraArgs = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // 同时支持 `--key value` 与 `--key=value`：客户端与手工调试各有一种习惯。
            string? inlineValue = null;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=');
                if (equals > 2)
                {
                    inlineValue = arg[(equals + 1)..];
                    arg = arg[..equals];
                }
            }

            switch (arg)
            {
                case BrowserHostSwitch:
                    break;

                case "--profile":
                    if (!TryReadValue(args, ref i, inlineValue, out var profileValue))
                        return new HostStartupParseResult(null, "缺少 --profile 的值：" + UsageError);

                    profile = profileValue;
                    break;

                case "--idle-seconds":
                    if (!TryReadValue(args, ref i, inlineValue, out var idleValue))
                        return new HostStartupParseResult(null, "缺少 --idle-seconds 的值：" + UsageError);

                    if (!int.TryParse(idleValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out idleSeconds)
                        || idleSeconds < 0)
                    {
                        return new HostStartupParseResult(null, "--idle-seconds 需要非负整数");
                    }

                    break;

                case "--headless":
                    if (!TryReadValue(args, ref i, inlineValue, out var headlessValue))
                        return new HostStartupParseResult(null, "缺少 --headless 的值：" + UsageError);

                    if (!bool.TryParse(headlessValue, out headless))
                        return new HostStartupParseResult(null, "--headless 需要 true 或 false");

                    break;

                case "--anti-detection":
                    if (!TryReadValue(args, ref i, inlineValue, out var antiDetectionValue))
                        return new HostStartupParseResult(null, "缺少 --anti-detection 的值：" + UsageError);

                    if (!bool.TryParse(antiDetectionValue, out antiDetection))
                        return new HostStartupParseResult(null, "--anti-detection 需要 true 或 false");

                    break;

                case "--executable":
                    if (!TryReadValue(args, ref i, inlineValue, out executablePath))
                        return new HostStartupParseResult(null, "缺少 --executable 的值：" + UsageError);

                    break;

                case "--user-agent":
                    if (!TryReadValue(args, ref i, inlineValue, out userAgent))
                        return new HostStartupParseResult(null, "缺少 --user-agent 的值：" + UsageError);

                    break;

                case "--extra-arg":
                    if (!TryReadValue(args, ref i, inlineValue, out var extra))
                        return new HostStartupParseResult(null, "缺少 --extra-arg 的值：" + UsageError);

                    extraArgs.Add(extra);
                    break;

                default:
                    // 未知参数忽略：不同入口会把 --urls 之类的宿主参数一起传下来，
                    // 为它们报错会让「同一份参数交给所有入口」变得不可能。
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(profile))
            return new HostStartupParseResult(null, "缺少 --profile <dir>。" + UsageError);

        return new HostStartupParseResult(
            new HostStartup(
                profile, idleSeconds, headless, executablePath, userAgent, antiDetection, [.. extraArgs]),
            null);
    }

    private static bool TryReadValue(string[] args, ref int index, string? inlineValue, out string value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static BrowserOptions CreateBrowserOptions(HostStartup startup, string profile)
        => new()
        {
            Headless = startup.Headless,
            ExecutablePath = startup.ExecutablePath,
            UserAgent = startup.UserAgent,
            ExtraArgs = startup.ExtraArgs.Length == 0 ? null : startup.ExtraArgs,

            // 宿主始终使用固定 profile（与 PersistLogin 无关）。
            // PersistLogin=true 只是为了复用它：宿主必须让 Chromium 的 user-data-dir
            // 就是 profile 本身，否则发现文件 hash 与真实目录会对不上。
            ProfileDirectory = profile,
            PersistLogin = true,

            // 页面级空闲回收属于客户端会话；宿主只按「有没有存活客户端」决定退出。
            IdleSeconds = 0,

            // 反检测是浏览器级设置（UA/启动参数），只能由宿主决定；
            // 设置页改了它就必须重启宿主才生效（客户端会比对 effective）。
            AntiDetection = startup.AntiDetection,
        };

    #endregion

    #region 迁移与进程工具

    /// <summary>
    /// 清理旧版本（客户端直连模式）留下的 PID 文件与孤儿浏览器；返回「必须拒绝启动」的原因。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧版本把浏览器当作客户端子进程，留 <c>.seeing-engine.pid</c> 记录 PID。
    /// 升级后第一次启动时如果那个浏览器还活着，它会一直占着 profile：
    /// 新宿主必须明确拒绝并告诉用户关闭旧实例，而不是反复撞锁。
    /// </para>
    /// <para>
    /// PID 文件里的进程已经不存在时，还要看命令行匹配 profile 的进程：
    /// 父进程已死 = 上次崩溃留下的孤儿，可以清理；父进程还活着 = 另一个实例在用，必须拒绝。
    /// </para>
    /// </remarks>
    private static string? TryClearLegacyInstance(string profile, ILogger log)
    {
        var pidFile = Path.Combine(profile, LegacyPidFileName);
        if (File.Exists(pidFile))
        {
            var legacyPid = ReadPidFile(pidFile);
            if (legacyPid > 0 && IsProcessAlive(legacyPid))
            {
                return $"检测到旧版本浏览器实例（PID {legacyPid}）仍在使用 profile {profile}。" +
                       "请先关闭旧实例（或结束该进程）后再启动。";
            }

            TryDeleteFile(pidFile);
            log.LogInformation("已清理旧版本 PID 文件 {Path}", pidFile);
        }

        var candidates = BrowserProcessProbe.FindByProfileDirectory(profile);
        foreach (var candidate in candidates)
        {
            if (!IsProcessAlive(candidate.Pid))
                continue;

            if (candidate.ParentPid <= 0)
            {
                return $"无法确认占用 profile {profile} 的浏览器进程 {candidate.Pid} 的父进程，" +
                       "拒绝自动清理。请手动关闭该进程后重试。";
            }

            if (IsProcessAlive(candidate.ParentPid))
            {
                return $"已有浏览器实例（PID {candidate.Pid}，父进程 {candidate.ParentPid}）" +
                       $"正在使用 profile {profile}，请先关闭它。";
            }

            log.LogWarning(
                "清理父进程已退出的孤儿浏览器进程 {Pid}（{Path}）",
                candidate.Pid,
                candidate.ExecutablePath);
            KillProcessTree(candidate.Pid);
        }

        return null;
    }

    private static int ReadPidFile(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                    return pid;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就当作没有记录：后面还有命令行探测兜底。
        }

        return 0;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不影响启动（撞锁时还有恢复路径）。
        }
    }

    private static bool IsProfileLockMessage(Exception exception)
    {
        // PuppeteerSharp 有时把 Chromium 的 stderr 只放在 InnerException 里：
        // 只看外层消息会漏判「profile 已被占用」，用户只能看到原始的 Failed to launch browser!
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (string.IsNullOrEmpty(message))
                continue;

            if (message.Contains("already running", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ProcessSingleton", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 权限不足（别的用户的进程）：存在但读不到 —— 按「活着」处理，绝不误杀。
            return true;
        }
    }

    private static void KillProcessTree(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // 进程已自行退出 / 权限不足：恢复路径会继续重试启动，不在这里中断。
        }
    }

    private static DateTimeOffset ResolveProcessStartedAt(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // 拿不到启动时刻：用「现在」近似。发现文件里的身份仍以 PID 为主。
            return DateTimeOffset.Now;
        }
    }

    /// <summary>
    /// POSIX 看门狗：spawn 同一份可执行文件的 <c>--watchdog</c> 模式。
    /// </summary>
    private static Process? TryStartWatchdog(int hostPid, int browserPid, ILogger log)
    {
        try
        {
            var startInfo = BuildSelfStartInfo([
                WatchdogSwitch,
                hostPid.ToString(CultureInfo.InvariantCulture),
                browserPid.ToString(CultureInfo.InvariantCulture),
            ]);

            var process = Process.Start(startInfo);
            if (process is null)
                log.LogWarning("看门狗未能启动：宿主被杀时浏览器可能残留");

            return process;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or IOException)
        {
            log.LogWarning(ex, "启动看门狗失败：宿主被杀时浏览器可能残留");
            return null;
        }
    }

    /// <summary>
    /// 拼「再次启动自己」的命令：优先 apphost，`dotnet` 场景回落到 <c>dotnet &lt;entry.dll&gt;</c>。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只看 <see cref="Environment.ProcessPath"/></b>：<c>dotnet run</c> / <c>vstest</c>
    /// 下它是 <c>dotnet.exe</c>，直接拿它加 <c>--watchdog</c> 会让 muxer 报「未知命令」。
    /// 那种场景必须显式带上入口 dll。
    /// </remarks>
    private static ProcessStartInfo BuildSelfStartInfo(IReadOnlyList<string> appArgs)
    {
        var processPath = Environment.ProcessPath;
        var isDotnetMuxer = string.IsNullOrWhiteSpace(processPath)
                            || string.Equals(
                                Path.GetFileNameWithoutExtension(processPath),
                                "dotnet",
                                StringComparison.OrdinalIgnoreCase);

        ProcessStartInfo startInfo;
        if (!isDotnetMuxer)
        {
            startInfo = new ProcessStartInfo(processPath!);
        }
        else
        {
            var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
            var entryDll = entryName is null
                ? null
                : Path.Combine(AppContext.BaseDirectory, entryName + ".dll");

            startInfo = new ProcessStartInfo(processPath ?? "dotnet");
            if (entryDll is not null && File.Exists(entryDll))
                startInfo.ArgumentList.Add(entryDll);
        }

        foreach (var arg in appArgs)
            startInfo.ArgumentList.Add(arg);

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return startInfo;
    }

    private static void WriteError(string message)
        => Console.Error.WriteLine("seeing browser-host: " + message);

    #endregion
}

/// <summary>
/// 宿主专用滚动日志（<c>~/.seeing/browser/host-&lt;hash&gt;.log</c>，5MB × 2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自己写而不是用 <c>Microsoft.Extensions.Logging</c> 的文件 provider</b>：
/// 宿主是「没有 UI、没有控制台」的后台进程（Desktop 甚至是 WinExe），
/// 出问题后唯一能看的就是这个文件。它必须满足两条硬约束：固定路径（客户端/设置页能找到）
/// 与有界体积（长期运行不能把磁盘吃满）。这点逻辑不值得引入额外依赖。
/// </para>
/// <para>
/// <b>为什么滚动是 5MB × 2 而不是无穷追加</b>：浏览器宿主可能连续运行数天，
/// 单文件无上限会让故障排查变成「先打开一个 2GB 文本」。
/// </para>
/// </remarks>
internal sealed class BrowserHostLog : ILogger, IDisposable
{
    /// <summary>单个文件上限（5MB）。</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>备份文件后缀（只有一份，覆盖式滚动）。</summary>
    private const string BackupSuffix = ".1";

    private readonly object _gate = new();
    private readonly string _path;

    private bool _disposed;

    public BrowserHostLog(string path) => _path = path;

    /// <summary>计算日志路径：与发现文件同目录，按 profile hash 区分。</summary>
    public static string ResolvePath(string baseDirectory, string profileHash)
        => Path.Combine(baseDirectory, $"host-{profileHash}.log");

    /// <summary>日志文件路径（诊断与报告用）。</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        ArgumentNullException.ThrowIfNull(formatter);

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [")
            .Append(LevelPrefix(logLevel))
            .Append("] ")
            .Append(formatter(state, exception));

        if (exception is not null)
            line.AppendLine().Append(exception);

        Write(line.ToString());
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                RollIfNeeded();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不进去不该让宿主退出：Debug 输出至少能在调试器里看到。
                Debug.WriteLine($"写浏览器宿主日志失败：{ex.Message}");
            }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxFileBytes)
            return;

        var backup = _path + BackupSuffix;
        if (File.Exists(backup))
            File.Delete(backup);

        File.Move(_path, backup);
    }

    private static string LevelPrefix(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
    }
}
