using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>浏览器可执行文件探测结果。</summary>
/// <param name="Path">可执行文件绝对路径（未找到为 <see langword="null"/>）。</param>
/// <param name="Source">来源说明（用于失败文案与诊断）。</param>
public readonly record struct BrowserExecutable(string? Path, string Source)
{
    /// <summary>是否找到。</summary>
    public bool Found => !string.IsNullOrWhiteSpace(Path);
}

/// <summary>
/// 进程级浏览器引擎：可执行文件探测、懒启动、崩溃自愈、统一关闭。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么懒启动</b>：启动 Chromium 要 200–400ms 与数十 MB 内存。绝大多数会话根本不碰浏览器，
/// 在模块 Activate 时就拉起进程是纯浪费。
/// </para>
/// <para>
/// <b>为什么全局只有一个 Browser</b>：会话隔离由 <c>BrowserContext</c> 提供（cookie/存储互不干扰）。
/// 每会话一个进程在并发会话下是内存炸弹。
/// </para>
/// <para>
/// <b>失败缓存</b>：启动失败后 10 秒内不再重试。否则模型连续调用几个 browser_* 工具会触发多次
/// 进程启动尝试，每次都要等超时，看起来像「界面卡死」。
/// </para>
/// </remarks>
public sealed class BrowserEngine : IAsyncDisposable
{
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(10);

    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly ILogger<BrowserEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IBrowser? _browser;

    /// <summary>当前浏览器进程实际使用的 profile 目录（<see langword="null"/> = 临时目录）。</summary>
    /// <remarks>
    /// 记录它的唯一目的是热重载：用户改了 <see cref="BrowserOptions.PersistLogin"/> 或
    /// <see cref="BrowserOptions.ProfileDirectory"/> 后，<c>IOptionsMonitor</c> 立刻返回新值，
    /// 但进程还在用旧 profile。不比对就会「配了新的却仍然读旧登录态」，
    /// 而症状是「换了 profile 目录但登录态还在」——看不出是没重启。
    /// </remarks>
    private string? _launchedProfileDirectory;

    /// <summary>
    /// 启动时用的「进程级签名」：凡是**只能在启动时生效**的配置都进这个串。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要它</b>：<c>Headless</c>、<c>AntiDetection</c>、<c>ExecutablePath</c>、
    /// <c>ExtraArgs</c> 都只能通过「重新拉起进程」生效。早期只比对 profile 目录，
    /// 于是用户在设置页把「无头模式」关掉、保存、重启配置也读到了新值，
    /// 但浏览器进程仍是旧的 —— 开关形同虚设（实测确认的缺口）。
    /// </para>
    /// <para>
    /// <b>为什么不逐项比较</b>：漏项是迟早的事（新增一个启动参数就容易忘）。
    /// 拼成一个签名串后，任何进入签名的配置变化都会触发重启，增删项只需改一处。
    /// </para>
    /// </remarks>
    private string? _launchedSignature;

    private DateTimeOffset? _failedAt;
    private string? _failureReason;

    /// <summary>创建引擎。</summary>
    /// <param name="options">配置（热重载：每次启动读当前值）。</param>
    /// <param name="logger">日志。</param>
    public BrowserEngine(IOptionsMonitor<BrowserOptions> options, ILogger<BrowserEngine>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<BrowserEngine>.Instance;
    }

    /// <summary>当前是否已有存活浏览器进程。</summary>
    public bool IsStarted => _browser is { IsConnected: true };

    /// <summary>
    /// 取（必要时启动）浏览器进程。
    /// </summary>
    /// <exception cref="BrowserUnavailableException">没有可用浏览器，或启动失败。</exception>
    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (_browser is { IsConnected: true } alive && !ProfileChanged())
            return alive;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true } again && !ProfileChanged())
                return again;

            // 失败冷却：短期内不再重试，直接复用上次的原因
            if (_failedAt is { } failedAt && DateTimeOffset.UtcNow - failedAt < FailureCooldown)
                throw new BrowserUnavailableException(_failureReason ?? "浏览器启动失败");

            // 旧实例（已断开 / profile 配置已变）先释放，避免句柄与 profile 锁泄漏
            await DisposeBrowserQuietlyAsync().ConfigureAwait(false);

            try
            {
                _browser = await LaunchAsync(cancellationToken).ConfigureAwait(false);
                _failedAt = null;
                _failureReason = null;
                _logger.LogInformation(
                    "浏览器已启动（{Headless}，{Profile}）",
                    _options.CurrentValue.Headless ? "无头" : "有头",
                    DescribeProfile(_launchedProfileDirectory));
                return _browser;
            }
            catch (BrowserUnavailableException ex)
            {
                Remember(ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                Remember($"启动浏览器失败：{ex.Message}");
                _logger.LogWarning(ex, "启动浏览器失败");
                throw new BrowserUnavailableException(_failureReason!);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 探测可用浏览器可执行文件。
    /// </summary>
    /// <param name="options">配置。</param>
    public static BrowserExecutable ResolveExecutable(BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return File.Exists(options.ExecutablePath)
                ? new BrowserExecutable(options.ExecutablePath, "配置指定")
                : new BrowserExecutable(null, $"配置指定的路径不存在：{options.ExecutablePath}");
        }

        foreach (var (path, source) in Candidates())
        {
            if (File.Exists(path))
                return new BrowserExecutable(path, source);
        }

        return new BrowserExecutable(null, "未找到 Edge / Chrome");
    }

    /// <summary>候选路径（按优先级）。抽成静态方法以便单测钉住顺序。</summary>
    internal static IEnumerable<(string Path, string Source)> Candidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // ① Edge（Windows 10/11 预装）
        if (!string.IsNullOrEmpty(programFilesX86))
            yield return (Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"), "系统 Edge（x86 目录）");

        if (!string.IsNullOrEmpty(programFiles))
            yield return (Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"), "系统 Edge");

        // ② Chrome
        if (!string.IsNullOrEmpty(programFiles))
            yield return (Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"), "系统 Chrome");

        if (!string.IsNullOrEmpty(programFilesX86))
            yield return (Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"), "系统 Chrome（x86 目录）");

        // ③ Chromium（PuppeteerSharp 下载到用户目录时）
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return (Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"), "用户级 Chrome");
            yield return (Path.Combine(localAppData, "Chromium", "Application", "chrome.exe"), "用户级 Chromium");
        }
    }

    private async Task<IBrowser> LaunchAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var executable = ResolveExecutable(options);

        var launchOptions = new LaunchOptions
        {
            Headless = options.Headless,
            Args = BuildLaunchArgs(options)
        };

        if (executable.Found)
        {
            launchOptions.ExecutablePath = executable.Path;
        }
        else
        {
            // 没有系统浏览器：明确失败，不静默去下一步载几百 MB
            throw new BrowserUnavailableException(
                $"{executable.Source}。请安装 Microsoft Edge 或 Google Chrome，" +
                $"或在 seeing.json 的 {BrowserOptions.SectionName}.ExecutablePath 指定浏览器路径");
        }

        // 持久 profile：让浏览器重启后仍保留 cookie / localStorage / 登录态。
        // 不指定时 PuppeteerSharp 每次建临时目录并销毁，等于每次都全新访客（实测登录态为 0）。
        var profileDirectory = options.PersistLogin ? ResolveProfileDirectory(options) : null;
        if (profileDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(profileDirectory);
                launchOptions.UserDataDir = profileDirectory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录不可用不该让整个模块不可用：退回临时 profile 并留警告。
                // （例如用户把 ProfileDirectory 指到只读盘；此时「能用但每次重新登录」远好过「完全用不了」。）
                profileDirectory = null;
                _logger.LogWarning(ex,
                    "持久 profile 目录不可用，本次改用临时 profile：{Directory}",
                    options.ProfileDirectory);
            }
        }

        _logger.LogDebug("使用浏览器：{Path}（{Source}）", executable.Path, executable.Source);

        IBrowser browser;
        try
        {
            browser = await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
        }
        catch (ProcessException ex) when (profileDirectory is not null && IsProfileLocked(ex))
        {
            // 持久 profile 的固有失败模式：上一个浏览器进程没退干净（崩溃 / 强杀 / 上一轮测试中断），
            // profile 目录被它锁着，Chromium 拒绝启动。
            //
            // 不做处理的话这里是**永久性**失败：失败冷却过后重试仍会撞同一把锁，
            // 用户只看到「浏览器起不来」，且没有任何提示说明是残留进程。
            // 只清理「命令行里带这个 profile 路径」的进程，绝不碰用户自己的浏览器。
            _logger.LogWarning(
                "profile 被残留浏览器进程占用，清理后重试：{Directory}", profileDirectory);

            var killed = await KillLeftoverProcessAsync(profileDirectory).ConfigureAwait(false);
            if (killed == 0)
                throw;

            // 等 profile 锁释放：条件轮询而不是固定等待
            browser = await RetryLaunchAfterCleanupAsync(launchOptions, profileDirectory, ex, cancellationToken)
                .ConfigureAwait(false);
        }

        browser.Disconnected += OnBrowserDisconnected;

        // 反检测的页面级部分：UA 伪装 + navigator/WebGL 指纹修补。
        // 必须在这里做（而不是在 BrowserSessionHost 建页时）：所有页面都该一致，
        // 包括 agent 用工具新建的页；且 UA 要在首次导航前生效。
        if (options.AntiDetection || !string.IsNullOrWhiteSpace(options.UserAgent))
        {
            await ApplyAntiDetectionAsync(browser, options).ConfigureAwait(false);
        }

        // 记录 PID：下次启动若撞到「已占用」，靠它精确定位残留进程。
        // 注意必须在 LaunchAsync 成功之后写 —— 启动失败时进程根本不存在。
        if (profileDirectory is not null)
            RecordBrowserProcess(browser, profileDirectory, executable.Path);

        // 只在启动成功后记录实际 profile 与签名：启动失败时若提前赋值，
        // ProfileChanged() 会误判「配置没变」而跳过重启（浏览器明明没起来）。
        _launchedProfileDirectory = profileDirectory;
        _launchedSignature = ComputeSignature(options);

        // 下载一律拒绝：agent 不应把文件写到未知位置。
        // 隔离上下文能经 BrowserContextOptions.DownloadBehavior 声明，但 DefaultContext
        // （持久 profile 必须用它，否则 cookie 不落盘）拿不到那个选项，只能用浏览器级 CDP。
        await DenyDownloadsAsync(browser).ConfigureAwait(false);

        return browser;
    }

    /// <summary>判定异常是否为「profile 已被另一浏览器进程占用」。</summary>
    internal static bool IsProfileLocked(Exception ex)
        => ex is ProcessException
           && ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase);

    /// <summary>记录本引擎启动的浏览器进程 PID（落在 profile 目录内，随 profile 一起失效）。</summary>
    private const string PidFileName = ".seeing-engine.pid";

    /// <summary>写 PID 记录，供下次启动时识别「上一个进程没退干净」。</summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要落盘而不是只放内存</b>：进程崩溃/被强杀时内存记录一起消失，
    /// 而 Chromium 对 profile 目录持有主锁 —— 残留进程会让后续每次启动都失败，
    /// 且没有任何线索指向「是某个残留进程」。落一个 PID 文件才能定位到具体进程。
    /// </para>
    /// <para>
    /// <b>为什么同时记可执行文件路径</b>：PID 会被系统复用。只凭 PID 杀进程可能误杀无关进程，
    /// 加路径校验把风险降到「同一浏览器在 PID 复用后恰好占用同路径」这种可忽略的情形。
    /// </para>
    /// </remarks>
    private void RecordBrowserProcess(IBrowser browser, string profileDirectory, string? executablePath)
    {
        try
        {
            var processId = browser.Process?.Id;
            if (processId is null or 0)
                return;

            // 用原子写（Seeing.Files.AtomicFileWrite 是仓库唯一允许的实现）：
            // 直写会在「写到一半被强杀」时留下半截文件，而下次启动正好要读它判断残留进程——
            // 半截内容会解析失败，等于静默失去自动清理能力。
            var path = Path.Combine(profileDirectory, PidFileName);
            Seeing.Files.AtomicFileWrite.WriteAllTextAtomic(
                path,
                $"{processId}\n{executablePath ?? string.Empty}",
                subject: "浏览器进程记录",
                logger: _logger);
        }
        catch (Exception ex)
        {
            // 记录失败只是失去「下次自动清理」的能力，不影响当前会话
            _logger.LogDebug(ex, "记录浏览器进程 PID 失败（忽略）");
        }
    }

    /// <summary>
    /// 结束「上次未退干净」的浏览器进程；返回被结束的数量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只杀 PID 文件里记录的那个进程</b>，不按进程名批量清理 ——
    /// 用户自己的 Edge 用同一可执行文件，按名杀会连带关掉他正在用的浏览器
    /// （未提交的表单、播放中的视频都是真实损失）。
    /// </para>
    /// <para>
    /// <b>为什么必须整树删除</b>：Chromium 是多进程架构，渲染/GPU/utility 子进程虽不持主锁，
    /// 但会继续占用 profile 内的文件句柄；只杀主进程会留下孤儿进程。
    /// </para>
    /// </remarks>
    private async Task<int> KillLeftoverProcessAsync(string profileDirectory)
    {
        var pidFile = Path.Combine(profileDirectory, PidFileName);
        if (!File.Exists(pidFile))
            return 0;

        try
        {
            var lines = File.ReadAllLines(pidFile);
            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var processId))
                return 0;

            var expectedPath = lines.Length > 1 ? lines[1].Trim() : string.Empty;

            using var process = System.Diagnostics.Process.GetProcessById(processId);

            // 路径校验：PID 可能已被复用给别的程序
            if (expectedPath.Length > 0)
            {
                string? actualPath = null;
                try { actualPath = process.MainModule?.FileName; }
                catch { /* 权限不足时拿不到；退化为仅按 PID 判定 */ }

                if (actualPath is not null
                    && !string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "PID {Pid} 已被别的程序复用（{Actual}），不清理", processId, actualPath);
                    return 0;
                }
            }

            process.Kill(entireProcessTree: true);

            // 用异步等待而不是 WaitForExit(ms)：后者在调用线程上同步阻塞最长 5 秒。
            // 这里的调用点是「清理残留进程」，通常毫秒级返回；给个上限防止卡死即可。
            try
            {
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 5 秒内没退干净：进程锁会由后续重试轮询兜住，这里不当作失败
                _logger.LogDebug("残留进程 PID {Pid} 在 5 秒内未退出", processId);
            }

            _logger.LogInformation("已结束残留的浏览器进程 PID {Pid}", processId);
            return 1;
        }
        catch (ArgumentException)
        {
            // 进程已不存在：正常情况（上次是干净退出的，只是没删掉 PID 文件）
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "清理残留浏览器进程失败（忽略）");
            return 0;
        }
        finally
        {
            try { File.Delete(pidFile); } catch { }
        }
    }

    /// <summary>清理残留进程后重试启动（条件轮询等锁释放，不猜固定时长）。</summary>
    /// <param name="launchOptions">启动参数。</param>
    /// <param name="profileDirectory">profile 目录（重试期间再次清理用）。</param>
    /// <param name="original">首次失败的异常（全部重试失败后作为内层异常）。</param>
    /// <param name="cancellationToken">取消令牌（用户取消时不必等满 20 轮 × 150ms）。</param>
    private async Task<IBrowser> RetryLaunchAfterCleanupAsync(
        LaunchOptions launchOptions,
        string profileDirectory,
        Exception original,
        CancellationToken cancellationToken)
    {
        Exception? last = original;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            // 轮询间隔（AGENTS.md 例外 E5）：命中即返回，不是「等状态就绪」的固定等待。
            // 必须接取消令牌：否则用户取消后仍要空转满 3 秒。
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

            try
            {
                return await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
            }
            catch (ProcessException ex)
            {
                last = ex;
                if (!IsProfileLocked(ex))
                    throw;

                // 锁仍被占：可能还有子进程未退，再清理一次（幂等）
                if (attempt % 5 == 4)
                    await KillLeftoverProcessAsync(profileDirectory).ConfigureAwait(false);
            }
        }

        throw new BrowserUnavailableException(
            $"profile 目录被残留浏览器进程占用，已尝试清理仍无法启动：{profileDirectory}。" +
            $"请手动结束后再试。原始错误：{last?.Message}");
    }

    /// <summary>
    /// 构造启动参数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要区分「必需」与「反检测」两类参数</b>：<c>--no-sandbox</c>、
    /// <c>--disable-gpu</c> 这类参数在启动命令行里可见，且分别关掉了沙箱与 GPU 渲染——
    /// 后者会让 WebGL 走软件渲染（SwiftShader），这是自动化检测的强特征。
    /// 但直接删掉它们会降低兼容性（部分容器环境没有 GPU、某些环境沙箱不可用），
    /// 所以做成开关而不是无条件移除。
    /// </para>
    /// <para>
    /// <b>用户实测背景</b>：「工作台里访问百度会触发真人验证，本机浏览器不会」。
    /// 主因是无头模式 + 自动化标志 + 干净 profile 的组合指纹，不是开发模式。
    /// </para>
    /// </remarks>
    internal static string[] BuildLaunchArgs(BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>
        {
            // 这三条与指纹无关，纯稳定性：避免 /dev/shm 太小导致崩溃、
            // 避免首次运行向导打断流程、避免后台节流让页面看起来「卡住」。
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-dev-shm-usage",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--mute-audio"
        };

        if (options.AntiDetection)
        {
            // ── 反检测：移除自动化标志、关闭可被指纹化的特性 ──

            // navigator.webdriver 的直接来源。这个特性的本意就是「告诉页面我在被自动化」。
            args.Add("--disable-blink-features=AutomationControlled");

            // 启动命令行里的 "enable-automation" 开关会被部分站点读取
            args.Add("--exclude-switches=enable-automation");

            // 不加载扩展（扩展列表本身也是指纹，但空列表比「有扩展」更常见）
            args.Add("--disable-extensions");

            // 保留 GPU（不传 --disable-gpu）：WebGL vendor/renderer 是强指纹，
            // 软件渲染（SwiftShader）几乎是自动化的铁证。
            // 若运行环境确实没有 GPU，Chromium 会自行回退，不需要我们预先关掉。

            // 注意：**不传 --no-sandbox**。它会被检测，且现代 Chromium 在多数平台
            // 已能正常启用沙箱。容器环境若确实需要，用户可自行在 ExtraArgs 里加。
        }
        else
        {
            // 保守档：兼容性优先（容器、无 GPU、受限沙箱环境）
            args.Add("--no-sandbox");
            args.Add("--disable-gpu");
            args.Add("--disable-extensions");
            args.Add("--disable-background-networking");
            args.Add("--disable-features=Translate,MediaRouter");
        }

        // 用户追加参数（放最后，可覆盖前面的默认值）
        if (options.ExtraArgs is { Length: > 0 } extra)
            args.AddRange(extra);

        return [.. args];
    }

    /// <summary>
    /// 应用反检测的页面级伪装：UA、navigator 属性、WebGL vendor/renderer。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么这些必须伪装</b>：反爬/验证码服务的首要判断依据是一组 JS 层指纹。
    /// 单个特征都不致命，但叠加起来足以判定「不是真人浏览器」：
    /// </para>
    /// <list type="bullet">
    /// <item><c>navigator.webdriver</c> —— 自动化最直接的标记</item>
    /// <item><c>navigator.plugins.length === 0</c> —— 真实 Chrome/Edge 至少有几个内置项</item>
    /// <item><c>navigator.languages</c> 为空或只有 en-US —— 与实际 locale 不符</item>
    /// <item>WebGL <c>UNMASKED_RENDERER_WEBGL</c> 为 SwiftShader —— 软件渲染的铁证</item>
    /// <item>UA 含 <c>HeadlessChrome</c> —— 无头模式的自我声明</item>
    /// </list>
    /// <para>
    /// <b>为什么用 <c>EvaluateFunctionOnNewDocumentAsync</c></b>：它让脚本在每个新文档创建时、
    /// 页面自己的脚本执行**之前**运行。用事后 <c>evaluate</c> 太晚——指纹检测脚本
    /// 通常在 <c>&lt;head&gt;</c> 里就执行完了。
    /// </para>
    /// <para>
    /// <b>为什么 <c>TargetCreated</c> 不够</b>（实测）：该事件回调里页面可能已经导航过，
    /// 此时脚本只影响「下一次」导航，而指纹检测往往就在首次导航时执行。
    /// 因此必须<b>同时</b>用 <c>browser.OnNewPageAsync</c> 之类的前置钩子做不到时，
    /// 采用「每次建页后立刻导航前」的顺序保证——见 <c>BrowserSessionHost</c> 的调用点。
    /// </para>
    /// <para>
    /// <b>诚实说明边界</b>：这是对抗性工程，没有一劳永逸。真正有效的三件事依次是：
    /// 有头模式（<see cref="BrowserOptions.Headless"/>=false）＞ 干净但真实的 profile ＞ 本伪装。
    /// IP 信誉与账号历史同样重要，那些不在本模块能力范围内。
    /// </para>
    /// </remarks>
    private async Task ApplyAntiDetectionAsync(IBrowser browser, BrowserOptions options)
    {
        try
        {
            var pages = await browser.PagesAsync().ConfigureAwait(false);
            foreach (var page in pages)
            {
                await ApplyToPageAsync(page, options, browser).ConfigureAwait(false);
            }

            // 之后新建的页面（agent 的工具、用户的地址栏）也要覆盖。
            // 注意这个钩子只能保证「后续导航」被覆盖；若页面在回调到达前已经导航，
            // 首次执行会漏。BrowserSessionHost 在建页后会再调一次 ApplyToPageAsync 兜底。
            //
            // 用**具名**处理器而不是匿名 lambda：匿名处理器无法退订，
            // 每次浏览器重启都会在事件上累积一个（并持有 options/browser 引用），
            // 长跑实例里表现为缓慢的委托链增长。
            void OnTargetCreated(object? _, TargetChangedArgs e) => _ = ApplyToNewTargetAsync(e, options, browser);

            browser.TargetCreated += OnTargetCreated;

            // 浏览器断开时摘掉：同一 BrowserEngine 会反复起停浏览器，
            // 不摘会让旧浏览器的处理器永远留在事件上（它已经不会再触发，但引用不会释放）。
            browser.Disconnected += (_, _) => browser.TargetCreated -= OnTargetCreated;
        }
        catch (Exception ex)
        {
            // 反检测失败不应阻断浏览器可用性
            _logger.LogWarning(ex, "应用反检测设置失败，浏览器将以默认指纹运行");
        }
    }

    /// <summary>对单个页面应用 UA 与 stealth 脚本（供宿主在建页后兜底调用）。</summary>
    /// <remarks>
    /// <b>为什么要有公开入口</b>：<c>TargetCreated</c> 事件到达时页面可能已开始导航，
    /// 首次页面的 stealth 会漏。宿主在「新建页面后、首次导航前」显式调一次才能保证覆盖。
    /// 幂等：重复设置同一 UA 与同一脚本无副作用。
    /// </remarks>
    public async Task ApplyAntiDetectionToPageAsync(IPage page)
    {
        if (!_options.CurrentValue.AntiDetection && string.IsNullOrWhiteSpace(_options.CurrentValue.UserAgent))
            return;

        await ApplyToPageAsync(page, _options.CurrentValue, _browser).ConfigureAwait(false);
    }

    /// <summary>处理新创建的 target：若是页面则应用反检测。</summary>
    private async Task ApplyToNewTargetAsync(TargetChangedArgs e, BrowserOptions options, IBrowser browser)
    {
        try
        {
            if (e.Target.Type != TargetType.Page)
                return;

            if (await e.Target.PageAsync().ConfigureAwait(false) is { } created)
                await ApplyToPageAsync(created, options, browser).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 事件回调里的异常不得外溢（会变成未观察任务异常）
            _logger.LogDebug(ex, "对新页面应用反检测失败（忽略）");
        }
    }

    private async Task ApplyToPageAsync(IPage page, BrowserOptions options, IBrowser? browser)
    {
        // ⚠ 顺序关键：**先注册脚本，再设 UA**。
        //
        // 实测：SetUserAgentAsync 会触发一次页面重载/重新初始化，
        // 如果在它之后注册 OnNewDocument 脚本，脚本会被这一次重载"吃掉"——
        // 表现为 chrome.runtime 等注入全部失效（而 webdriver 偶尔残留，更让人误判）。
        // 先把脚本挂上，后续重载也会重新执行它。
        var identifier = await page.EvaluateFunctionOnNewDocumentAsync(StealthScript).ConfigureAwait(false);
        _logger.LogDebug(
            "反检测脚本已注册（{Identifier}），页面 {Url}", identifier, page.Url);

        // UA：优先用配置值；否则取浏览器的真实 UA 再去掉 Headless 标记
        var userAgent = options.UserAgent;
        if (string.IsNullOrWhiteSpace(userAgent) && browser is not null)
        {
            try
            {
                userAgent = await browser.GetUserAgentAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    userAgent = userAgent
                        .Replace("HeadlessChrome", "Chrome", StringComparison.OrdinalIgnoreCase)
                        .Replace("HeadlessEdg", "Edg", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取浏览器 UA 失败，沿用默认");
            }
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
            await page.SetUserAgentAsync(userAgent).ConfigureAwait(false);
    }

    /// <summary>在页面脚本执行前修补常见自动化指纹。</summary>
    private const string StealthScript = """
        (() => {
          // ── navigator.webdriver ──
          // 用 defineProperty 而不是赋值：部分检测脚本会检查 getter 是否被替换成普通值。
          try {
            Object.defineProperty(navigator, 'webdriver', {
              get: () => undefined,
              configurable: true
            });
          } catch {}

          // ── navigator.languages ──
          // 空数组或只有 en-US 与真实浏览器不符（中文环境至少是 zh-CN 在前）。
          try {
            Object.defineProperty(navigator, 'languages', {
              get: () => ['zh-CN', 'zh', 'en-US', 'en'],
              configurable: true
            });
          } catch {}

          // ── navigator.plugins ──
          // 真实 Chrome/Edge 的 plugins 至少有 PDF 相关几项；无头模式常为空。
          try {
            const pluginData = [
              { name: 'PDF Viewer', filename: 'internal-pdf-viewer', desc: 'Portable Document Format' },
              { name: 'Chrome PDF Viewer', filename: 'internal-pdf-viewer', desc: 'Portable Document Format' },
              { name: 'Chromium PDF Viewer', filename: 'internal-pdf-viewer', desc: 'Portable Document Format' }
            ];
            const plugins = pluginData.map(p => {
              const plugin = Object.create(Plugin.prototype);
              Object.defineProperties(plugin, {
                name: { value: p.name, enumerable: true },
                filename: { value: p.filename, enumerable: true },
                description: { value: p.desc, enumerable: true },
                length: { value: 1, enumerable: true }
              });
              return plugin;
            });
            Object.defineProperty(navigator, 'plugins', {
              get: () => plugins,
              configurable: true
            });
          } catch {}

          // ── WebGL vendor / renderer ──
          // SwiftShader 是 Chromium 的软件渲染后端，出现在 UNMASKED_* 参数里几乎等于自报自动化。
          try {
            const patch = (proto) => {
              const orig = proto.getParameter;
              proto.getParameter = function (param) {
                // UNMASKED_VENDOR_WEBGL = 37445, UNMASKED_RENDERER_WEBGL = 37446
                if (param === 37445) return 'Intel Inc.';
                if (param === 37446) return 'Intel Iris OpenGL Engine';
                return orig.call(this, param);
              };
            };
            if (window.WebGLRenderingContext) patch(WebGLRenderingContext.prototype);
            if (window.WebGL2RenderingContext) patch(WebGL2RenderingContext.prototype);
          } catch {}

          // ── chrome.runtime ──
          // 有头 Chrome/Edge 总有 window.chrome，其缺失是无头特征之一。
          try {
            if (!window.chrome) window.chrome = {};
            if (!window.chrome.runtime) window.chrome.runtime = {};
          } catch {}

          // ── permissions.query 的 notification 行为差异 ──
          try {
            const origQuery = window.navigator.permissions.query;
            window.navigator.permissions.query = (params) =>
              params && params.name === 'notifications'
                ? Promise.resolve({ state: Notification.permission })
                : origQuery.call(window.navigator.permissions, params);
          } catch {}
        })();
        """;

    /// <summary>解析持久 profile 目录（配置优先，否则 <c>{用户目录}/.seeing/browser-profile</c>）。</summary>
    /// <remarks>
    /// 抽成静态方法便于单测钉住默认位置与相对路径展开，而不必真启动浏览器。
    /// <para>
    /// <b>为什么是 public</b>：设置页要展示「当前生效的 profile 路径」与实际解析结果，
    /// 否则用户填了相对路径或留空时无从判断最终落在哪里。宿主不能自己拼路径——
    /// 那会让「默认位置」出现第二份实现，两边漂移时症状是「设置页显示的位置与实际用的不同」。
    /// </para>
    /// </remarks>
    public static string ResolveProfileDirectory(BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ProfileDirectory))
            return Path.GetFullPath(options.ProfileDirectory);

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".seeing", "browser-profile");
    }

    /// <summary>判定「配置里的进程级设置是否已与当前进程不一致」（热重载需要重启浏览器）。</summary>
    /// <remarks>
    /// 覆盖所有**只能在启动时生效**的项：Headless / AntiDetection / ExecutablePath /
    /// ExtraArgs / UserAgent / profile 选择。视口、超时、空闲回收这些运行期读取的项不在其中
    /// —— 它们改了不需要重启。
    /// </remarks>
    private bool ProfileChanged()
        => !string.Equals(ComputeSignature(_options.CurrentValue), _launchedSignature, StringComparison.Ordinal);

    /// <summary>计算进程级配置签名（只含启动时才能生效的项）。</summary>
    /// <remarks>
    /// <b>为什么公开</b>：设置页要判断「哪些改动需要重启才能生效」，从而给出准确提示。
    /// 若宿主自己复制一份判断逻辑，两边字段集合必然漂移——症状是「提示说不用重启，
    /// 但实际没生效」或反过来。签名只有一份定义，宿主只比对结果。
    /// </remarks>
    public static string ComputeSignature(BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var profile = options.PersistLogin ? ResolveProfileDirectory(options) : "(temp)";
        var args = options.ExtraArgs is { Length: > 0 } extra ? string.Join('\u001f', extra) : string.Empty;

        return string.Join('\u001e',
            options.Headless,
            options.AntiDetection,
            options.ExecutablePath ?? string.Empty,
            options.UserAgent ?? string.Empty,
            args,
            profile);
    }

    /// <summary>浏览器级拒绝下载（默认上下文没有 <c>BrowserContextOptions</c> 可用）。</summary>
    private async Task DenyDownloadsAsync(IBrowser browser)
    {
        try
        {
            var cdp = await browser.CreateCDPSessionAsync().ConfigureAwait(false);
            await cdp.SendAsync("Browser.setDownloadBehavior", new Dictionary<string, object>
            {
                ["behavior"] = "deny"
            }).ConfigureAwait(false);
            await cdp.DetachAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 失败不阻断启动：下载只是额外护栏，页面仍可正常使用。
            _logger.LogDebug(ex, "设置浏览器级下载拒绝失败（忽略）");
        }
    }

    private static string DescribeProfile(string? directory)
        => directory is null ? "临时 profile（重启即丢失登录态）" : $"profile {directory}";

    private void OnBrowserDisconnected(object? sender, EventArgs e)
    {
        _logger.LogWarning("浏览器进程已断开；下次调用将重新启动");

        // 只清「确实是当前这个浏览器」的引用：旧实例迟到的 Disconnected 事件可能在
        // GetBrowserAsync 已把新实例赋给 _browser 之后才到达，无条件置 null 会
        // 把健康的引用清掉（下次调用又白重启一次浏览器）。
        //
        // 用比较交换而不是取锁：本方法在 CDP 回调线程上执行，
        // 而 _gate 持有时会 await 浏览器操作——在那里等锁会与 CDP 线程互相等待。
        Interlocked.CompareExchange(ref _browser, null, sender as IBrowser);
    }

    private void Remember(string reason)
    {
        _failedAt = DateTimeOffset.UtcNow;
        _failureReason = reason;
    }

    private async Task DisposeBrowserQuietlyAsync()
    {
        if (_browser is null)
            return;

        var browser = _browser;
        var profileDirectory = _launchedProfileDirectory;
        _browser = null;
        _launchedProfileDirectory = null;
        _launchedSignature = null;
        browser.Disconnected -= OnBrowserDisconnected;

        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
            await browser.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭浏览器时出现异常（忽略）");
        }

        // 干净关停后删掉 PID 记录：留着的后果是下次启动把它当成「残留进程」去查，
        // 而那个 PID 可能已被系统复用给无关程序（虽有路径校验，但不必制造这种风险）。
        if (profileDirectory is not null)
        {
            try { File.Delete(Path.Combine(profileDirectory, PidFileName)); }
            catch { /* 删不掉不影响功能 */ }
        }
    }

    /// <summary>
    /// 关停浏览器进程但保持引擎可再次使用（模块停用 / 热重载路径）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DisposeAsync"/> 的区别：这里<b>不释放</b>内部信号量。
    /// 模块停用后可能被重新启用，DI 单例仍是同一个实例；释放信号量会让后续调用永久失败。
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeBrowserQuietlyAsync().ConfigureAwait(false);
            _failedAt = null;
            _failureReason = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeBrowserQuietlyAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

/// <summary>没有可用浏览器 / 启动失败。</summary>
public sealed class BrowserUnavailableException : Exception
{
    /// <summary>创建异常。</summary>
    public BrowserUnavailableException(string message) : base(message)
    {
    }
}
