using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 浏览器宿主进程：拥有 Chromium 进程，并以 <c>--remote-debugging-port=0</c> 在 loopback 上
/// 暴露 CDP endpoint，供多个客户端 <c>ConnectAsync</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么由宿主进程拥有浏览器</b>：多个客户端（桌面 / TUI / WebUI 多窗口）要共享同一份登录态，
/// 就只能共享同一个浏览器进程。若每个客户端各自 <c>LaunchAsync</c>，同一 profile 目录会互相抢锁
/// （Chromium 直接拒绝第二次启动），而且谁退出都不该带走别人的页面。
/// 客户端只 <c>ConnectAsync</c> + <c>Disconnect()</c>，绝不 <c>CloseAsync</c>。
/// </para>
/// <para>
/// <b>为什么要自己读 <c>DevToolsActivePort</c></b>：端口取 0 让内核分配，真实端口只有 Chromium
/// 写进 profile 目录的这个文件里。客户端拿到 endpoint 才能走 TCP 而不是 PuppeteerSharp
/// 的 <c>Pipe</c>（pipe 只在本进程内有效，跨进程无法共享）。
/// </para>
/// <para>
/// <b>为什么进程清理要「关 → 杀 → 释放 job」三步</b>：<c>CloseAsync</c> 是优雅退出，
/// 但浏览器可能因为模态对话框 / 未响应卡住；超时后 <c>Kill(entireProcessTree: true)</c> 兜住
/// renderer 等子进程；最后释放 <see cref="BrowserJobObject"/> 让内核把漏网的也清掉。
/// 只做第一步的结果是「宿主退出后 profile 仍被占用」，下次启动直接报
/// <c>The browser is already running for ...</c>。
/// </para>
/// <para>
/// <b>不自动 <see cref="BrowserJobObject.AssignCurrentProcess"/></b>：该方法会把<b>调用进程自己</b>
/// 放进带 KILL_ON_JOB_CLOSE 的 job，dispose 即自杀。宿主进程（<c>--browser-host</c> 模式）
/// 必须在调用本类之前自行 assign，测试与客户端进程绝不调用。
/// </para>
/// </remarks>
public sealed class BrowserProcessHost : IAsyncDisposable
{
    /// <summary>Chromium 在 profile 目录里写下的端口/路径文件。</summary>
    private const string DevToolsActivePortFileName = "DevToolsActivePort";

    /// <summary><c>DevToolsActivePort</c> 第二行的固定前缀（browser 级 CDP 路径）。</summary>
    private const string DevToolsBrowserPathPrefix = "/devtools/browser/";

    /// <summary>轮询 endpoint 的上限。</summary>
    private static readonly TimeSpan EndpointWaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>轮询间隔（实现细节，命中即返回）。</summary>
    private static readonly TimeSpan EndpointPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>等这么久还读不到文件，就改用 Puppeteer 已解析的 endpoint（避免白等满 10 秒）。</summary>
    private static readonly TimeSpan EndpointFallbackDelay = TimeSpan.FromSeconds(1);

    /// <summary>等待 <c>CloseAsync</c> 优雅退出的宽限期。</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    /// <summary>强杀后等待进程消失的上限。</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(3);

    private readonly IBrowser _browser;
    private readonly Process? _process;
    private readonly BrowserJobObject? _job;
    private readonly string? _temporaryProfileDirectory;
    private readonly ILogger? _logger;

    private int _disposed;

    private BrowserProcessHost(
        IBrowser browser,
        Process? process,
        string endpoint,
        string profileDirectory,
        BrowserJobObject? job,
        string? temporaryProfileDirectory,
        ILogger? logger)
    {
        _browser = browser;
        _process = process;
        _job = job;
        Endpoint = endpoint;
        ProfileDirectory = profileDirectory;
        _temporaryProfileDirectory = temporaryProfileDirectory;
        _logger = logger;
        BrowserPid = process?.Id ?? 0;
        BrowserStartedAt = ReadStartTime(process);
    }

    /// <summary>已连接的浏览器实例（客户端侧不应在共享宿主上关闭它）。</summary>
    public IBrowser Browser => _browser;

    /// <summary>loopback CDP endpoint（<c>ws://127.0.0.1:&lt;port&gt;/devtools/browser/...</c>）。</summary>
    public string Endpoint { get; }

    /// <summary>浏览器主进程 PID；取不到时为 0。</summary>
    public int BrowserPid { get; }

    /// <summary>浏览器进程启动时间（本地时区偏移；取不到时用「现在」近似）。</summary>
    public DateTimeOffset BrowserStartedAt { get; }

    /// <summary>本次运行实际使用的 profile 目录（临时 profile 也在这里）。</summary>
    public string ProfileDirectory { get; }

    /// <summary>
    /// 启动浏览器并等它写出 endpoint。
    /// </summary>
    /// <param name="options">配置（headless / 可执行文件 / 启动参数 / profile）。</param>
    /// <param name="ct">取消令牌（仅用于启动前的检查；启动过程本身受 Puppeteer 超时约束）。</param>
    /// <param name="logger">日志（可空）。</param>
    /// <param name="hostAlreadyInJob">
    /// 宿主进程是否<b>已尝试</b>把自己放进 <see cref="BrowserJobObject.AssignCurrentProcess"/> 的 job 里。
    /// 仅当内核也能证明（<see cref="BrowserJobObject.IsCurrentProcessInJob"/>）时才跳过嵌套 job；
    /// 证明不了（self-assign 失败、受限容器）就回落到为浏览器进程单独建 job。
    /// </param>
    /// <exception cref="BrowserExecutableMissingException">没有可用的浏览器可执行文件。</exception>
    /// <exception cref="BrowserUnavailableException">启动失败，或 10 秒内拿不到 endpoint。</exception>
    public static async Task<BrowserProcessHost> StartAsync(
        BrowserOptions options, CancellationToken ct, ILogger? logger = null, bool hostAlreadyInJob = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var executable = BrowserEngine.ResolveExecutable(options);
        if (!executable.Found || executable.Path is null)
        {
            // 与 BrowserEngine 同一套文案：宿主与直连模式对「怎么装浏览器」的说法必须一致，
            // 否则用户按一处提示装完，另一处仍说找不到。
            throw new BrowserExecutableMissingException(
                $"{executable.Source}。{BrowserEngine.InstallHint()}" +
                $"或在 seeing.json 的 {BrowserOptions.SectionName}.ExecutablePath 指定浏览器路径");
        }

        var (profileDirectory, temporaryProfileDirectory) = ResolveProfile(options);
        var devToolsPortFile = Path.Combine(profileDirectory, DevToolsActivePortFileName);

        // 上一次运行留下的端口文件必须先删：否则本轮轮询会立刻读到旧端口，
        // 把「还没起来」误判成「已起来」，客户端随后连到一个不存在的端口。
        TryDeleteFile(devToolsPortFile);

        var args = new List<string>(BrowserEngine.BuildLaunchArgs(options))
        {
            // 端口 0 = 内核分配；endpoint 从 DevToolsActivePort 读。
            // PuppeteerSharp 的默认参数里也有这一条，这里显式声明是因为它是本类的契约，
            // 不该依赖默认参数集合（升级时默认值一变，endpoint 就再也读不到）。
            "--remote-debugging-port=0",
        };

        var launchOptions = new LaunchOptions
        {
            Headless = options.Headless,
            Args = [.. args],
            ExecutablePath = executable.Path,
            UserDataDir = profileDirectory,
            // 必须走 TCP：pipe 是进程内句柄，其他客户端根本连不上。
            Pipe = false,
            // 不预先创建 about:blank 标签页：共享宿主下每个客户端自己建窗口，
            // 多出来的空白页会在有头模式下变成一个没人认领的可见窗口。
            WaitForInitialPage = false,
        };

        IBrowser? browser = null;
        BrowserJobObject? job = null;
        try
        {
            try
            {
                browser = await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
            }
            catch (ProcessException ex) when (BrowserEngine.TryExplainLaunchFailure(ex, out var explanation))
            {
                // 原始异常只有 "Failed to launch browser!"，用户看不出要装什么库 / 开什么显示。
                logger?.LogWarning(ex, "启动浏览器失败：{Reason}", explanation);
                throw new BrowserUnavailableException(explanation!);
            }

            // 启动成功后才检查取消：取消发生在这里时由下面的 catch 负责收掉刚起来的进程。
            ct.ThrowIfCancellationRequested();

            var browserProcess = browser.Process;
            var hostProvenInJob = BrowserJobObject.IsCurrentProcessInJob();
            if (hostAlreadyInJob && !hostProvenInJob)
            {
                // 宿主 self-assign 失败时不能默默跳过嵌套 Job：那条路径上浏览器会完全失去兜底。
                logger?.LogWarning(
                    "宿主声称已在 Job 内，但内核证明不了（IsProcessInJob=false）；回落到为浏览器进程单独创建 Job");
            }

            job = ShouldSkipNestedJob(hostAlreadyInJob, hostProvenInJob)
                ? SkipJobBecauseHostOwnsOne(browserProcess, logger)
                : CreateJobFor(browserProcess, logger);
            var endpoint = await WaitForEndpointAsync(devToolsPortFile, browser, ct).ConfigureAwait(false);

            logger?.LogDebug(
                "浏览器宿主已就绪（pid={Pid}，{Endpoint}）", browserProcess?.Id ?? 0, endpoint);

            return new BrowserProcessHost(
                browser, browserProcess, endpoint, profileDirectory, job, temporaryProfileDirectory, logger);
        }
        catch
        {
            // 失败路径必须自己收拾：返回对象没建出来，DisposeAsync 不会被调用。
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 已经失败一次了，这里再抛只会淹没真正的失败原因
                }

                try
                {
                    await browser.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 同上
                }
            }

            if (temporaryProfileDirectory is not null)
                TryDeleteDirectory(temporaryProfileDirectory);

            // job 句柄必须显式关：它没有终结器，泄漏意味着「KILL_ON_JOB_CLOSE」永远不会触发。
            job?.Dispose();

            throw;
        }
    }

    /// <summary>
    /// 解析 <c>DevToolsActivePort</c> 内容为 CDP endpoint；格式不对返回 <see langword="null"/>。
    /// </summary>
    /// <param name="fileContent">文件内容：第一行端口，第二行 browser 路径。</param>
    /// <remarks>
    /// <para>
    /// <b>两行都必须严格匹配</b>：第一行纯数字端口（1–65535），第二行以 <c>/devtools/browser/</c> 开头。
    /// 只校验「像路径」会把 <c>/foo</c> 这类内容也放行，客户端拿到一个连不上的 endpoint，
    /// 症状是「浏览器明明起来了却一直连不上」，比直接返回 null（重试/报错）难查得多。
    /// </para>
    /// <para>
    /// 固定用 <c>127.0.0.1</c> 而不是 <c>localhost</c>：Chromium 默认只监听 loopback，
    /// 而 <c>localhost</c> 在部分环境会先解析到 <c>::1</c>，那是个不会有人监听的地址，
    /// 表现为「连接被拒绝」但端口明明是对的。
    /// </para>
    /// </remarks>
    internal static string? ParseDevToolsEndpoint(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return null;

        var lines = fileContent
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length < 2)
            return null;

        if (!int.TryParse(lines[0], out var port) || port is < 1 or > 65535)
            return null;

        var path = lines[1];
        if (!path.StartsWith(DevToolsBrowserPathPrefix, StringComparison.Ordinal))
            return null;

        return $"ws://127.0.0.1:{port}{path}";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 最外层兜底：调用方是「宿主正在退出」这条路径，任何异常都不该让后续清理被跳过。
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "关闭浏览器宿主时出现未预期的异常（忽略）");
        }
    }

    /// <summary>关停三步：优雅关 → 强杀 → 释放 job；每步独立兜底，job/进程/临时目录走 finally。</summary>
    private async ValueTask DisposeCoreAsync()
    {
        try
        {
            await _browser.CloseAsync().WaitAsync(CloseGrace).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "关闭浏览器超时或失败，转为强杀");
        }

        try
        {
            if (_process is not null && !HasExited(_process))
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "强杀浏览器进程失败（可能已自行退出）");
                }

                await WaitForExitAsync(_process, KillGrace).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "等待浏览器进程退出失败（忽略）");
        }

        try
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "释放浏览器连接失败（忽略）");
        }
        finally
        {
            // 无论上面哪一步怎么失败，句柄与临时目录都必须收掉：Job 没有终结器，
            // 泄漏即等于 KILL_ON_JOB_CLOSE 永不触发；临时 profile 不删则下次仍会撞锁。
            try
            {
                _job?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "释放 Job 句柄失败（忽略）");
            }

            try
            {
                _process?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "释放进程句柄失败（忽略）");
            }

            if (_temporaryProfileDirectory is not null)
                TryDeleteDirectory(_temporaryProfileDirectory);
        }
    }

    /// <summary>解析 profile 目录；<see cref="BrowserOptions.PersistLogin"/> 关闭时用临时目录（并记录以便删除）。</summary>
    private static (string ProfileDirectory, string? TemporaryDirectory) ResolveProfile(BrowserOptions options)
    {
        if (options.PersistLogin)
        {
            var directory = BrowserEngine.ResolveProfileDirectory(options);
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new BrowserUnavailableException(
                    $"浏览器 profile 目录不可用：{directory}（{ex.Message}）");
            }

            return (directory, null);
        }

        var temporary = Path.Combine(
            Path.GetTempPath(), "seeing-browser-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        return (temporary, temporary);
    }

    /// <summary>
    /// 是否跳过嵌套 Job：「调用方声称宿主已在 Job 里」<b>且</b>内核能证明这句话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不直接相信调用方</b>：<c>hostAlreadyInJob: true</c> 只表示宿主<b>尝试</b>
    /// <see cref="BrowserJobObject.AssignCurrentProcess"/> 过。该调用失败（受限容器、
    /// 外层 job 禁止 breakaway、权限不足）时宿主自身不在任何 Job 里，此时再跳过嵌套 Job
    /// 就等于「完全没有兜底」——宿主被杀后浏览器残留，且日志里连一句警告都没有。
    /// </para>
    /// <para>
    /// 抽成纯函数只为让这条判定能在无 Job 的测试环境里被钉住（自 assign 会杀掉测试进程）。
    /// </para>
    /// </remarks>
    internal static bool ShouldSkipNestedJob(bool hostAlreadyInJob, bool hostProvenInJob)
        => hostAlreadyInJob && hostProvenInJob;

    /// <summary>
    /// 宿主已自行 assign 时的分支：不建嵌套 job。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是「再建一个也无害」</b>：嵌套 job 在受限环境里可能失败，而失败只能落进
    /// 那句「宿主被杀时浏览器可能残留」的警告里 —— 事实上浏览器已被祖先 job 覆盖，
    /// 这条警告会把正常情况描述成故障，让排查方向直接跑偏。跳过并记 Debug 最诚实。
    /// </remarks>
    private static BrowserJobObject? SkipJobBecauseHostOwnsOne(Process? browserProcess, ILogger? logger)
    {
        logger?.LogDebug(
            "宿主进程已自 assign Job，浏览器进程 {Pid} 由祖先 Job 覆盖，跳过嵌套 Job",
            browserProcess?.Id ?? 0);
        return null;
    }

    /// <summary>
    /// 为浏览器进程建 job 并 assign。
    /// </summary>
    /// <remarks>
    /// <b>为什么在启动之后才 assign</b>：Puppeteer 不接受外部的「先入 job 再 spawn」回调，
    /// 而本类又不能把宿主自己 assign 进 job（测试会自杀）。代价是 assign 之前已创建的 renderer
    /// 不在 job 内 —— 由 <c>DisposeAsync</c> 的 <c>Kill(entireProcessTree: true)</c> 兜住。
    /// 宿主进程自己 assign 的那份 job 才是「宿主被杀」路径上的强保证。
    /// </remarks>
    private static BrowserJobObject? CreateJobFor(Process? browserProcess, ILogger? logger)
    {
        var job = BrowserJobObject.Create();
        if (job is null)
            return null;

        if (browserProcess is null)
        {
            // 拿不到进程句柄（极端情况）：留着空 job 无意义。
            job.Dispose();
            return null;
        }

        if (!job.TryAssign(browserProcess.Id))
        {
            logger?.LogWarning(
                "无法把浏览器进程 {Pid} 放进 Job（没有祖先 Job 兜底时，宿主被杀浏览器可能残留）",
                browserProcess.Id);
            job.Dispose();
            return null;
        }

        return job;
    }

    /// <summary>轮询 <c>DevToolsActivePort</c>，必要时回落到 Puppeteer 已解析出的 endpoint。</summary>
    private static async Task<string> WaitForEndpointAsync(
        string devToolsPortFile, IBrowser browser, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < EndpointWaitLimit)
        {
            ct.ThrowIfCancellationRequested();

            var endpoint = TryReadEndpoint(devToolsPortFile);
            if (endpoint is not null)
                return endpoint;

            // 文件迟迟不出现时尽早改用 Puppeteer 已解析出的同一个值：两边本来就是同一个来源
            // （PuppeteerSharp 也是读这个文件），没必要为了「必须从文件读」白等满 10 秒。
            if (stopwatch.Elapsed >= EndpointFallbackDelay
                && browser.WebSocketEndpoint is { } early)
            {
                return early;
            }

            await Task.Delay(EndpointPollInterval, ct).ConfigureAwait(false);
        }

        // 兜底：某些版本读完会删掉该文件，那时只有这个来源可用。
        return browser.WebSocketEndpoint
            ?? throw new BrowserUnavailableException(
                $"浏览器已启动，但 {EndpointWaitLimit.TotalSeconds:0} 秒内未读到 " +
                $"{DevToolsActivePortFileName}，且未拿到可用的 CDP endpoint。");
    }

    /// <summary>读一次端口文件；不完整（Chromium 正在写）时返回 <see langword="null"/> 等下一轮。</summary>
    private static string? TryReadEndpoint(string devToolsPortFile)
    {
        try
        {
            if (!File.Exists(devToolsPortFile))
                return null;

            // 用 FileShare.ReadWrite 打开：Chromium 可能仍持有写入句柄，
            // 默认的独占读会直接抛 IOException（表现为「文件明明存在却读不到」）。
            using var stream = new FileStream(
                devToolsPortFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return ParseDevToolsEndpoint(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset ReadStartTime(Process? process)
    {
        if (process is not null)
        {
            try
            {
                return new DateTimeOffset(process.StartTime);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // 进程已退出或权限不足：回落到近似时间（发现文件里它只用于展示）
            }
        }

        return DateTimeOffset.Now;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 仍未退出：交给 job 释放兜底
        }
        catch (InvalidOperationException)
        {
            // 进程已退出并被释放
        }
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
            // 删不掉不影响启动：轮询会一直读到旧内容，但那说明 profile 正被别人占用，
            // 启动本身也会失败并在那里给出明确原因。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时 profile 残留不影响宿主退出
        }
    }
}
