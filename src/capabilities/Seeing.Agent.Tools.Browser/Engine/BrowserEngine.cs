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

/// <summary>候选路径所属平台（用于把平台分支做成可测参数）。</summary>
public enum BrowserPlatform
{
    /// <summary>Windows（Edge 预装优先）。</summary>
    Windows,

    /// <summary>macOS（/Applications 下的应用包）。</summary>
    MacOS,

    /// <summary>Linux（发行版包 / Snap / PATH / 下载缓存）。</summary>
    Linux
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

    /// <summary>上次失败是否应跳过冷却（可恢复：缺可执行文件 / profile 锁）。</summary>
    /// <remarks>
    /// 找不到可执行文件：装完就该立刻可用。profile 锁：残留进程一退就该立刻可启动。
    /// 这两类进 10 秒冷却只会让用户连点空转（切会话再开时尤其明显）。
    /// </remarks>
    private bool _skipFailureCooldown;

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

            // 失败冷却：短期内不再重试，直接复用上次的原因。
            //
            // 例外（跳过冷却）：可恢复失败——「找不到可执行文件」（装完就该立刻可用）、
            // 「profile 被残留进程占用」（锁马上会放，进冷却只会让切会话再开连点 10 秒全失败）。
            if (_failedAt is { } failedAt
                && DateTimeOffset.UtcNow - failedAt < FailureCooldown
                && !_skipFailureCooldown)
            {
                throw new BrowserUnavailableException(_failureReason ?? "浏览器启动失败");
            }

            // 旧实例（已断开 / profile 配置已变）先释放，避免句柄与 profile 锁泄漏
            await DisposeBrowserQuietlyAsync().ConfigureAwait(false);

            try
            {
                _browser = await LaunchAsync(cancellationToken).ConfigureAwait(false);
                _failedAt = null;
                _failureReason = null;
                _skipFailureCooldown = false;
                _logger.LogInformation(
                    "浏览器已启动（{Headless}，{Profile}）",
                    _options.CurrentValue.Headless ? "无头" : "有头",
                    DescribeProfile(_launchedProfileDirectory));
                return _browser;
            }
            catch (BrowserUnavailableException ex)
            {
                Remember(ex.Message, skipCooldown: ShouldSkipFailureCooldown(ex));
                throw;
            }
            catch (Exception ex)
            {
                var message = $"启动浏览器失败：{ex.Message}";
                Remember(message, skipCooldown: IsProfileLockFailure(ex) || IsProfileLockFailure(message));
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
    /// <remarks>
    /// <para>
    /// <b>每次调用都重新探测磁盘，不缓存结果</b>：否则「服务启动时还没装浏览器、运行中才装」
    /// 这种顺序下永远探测不到（Linux 服务器上的实际痛点）。探测只是若干次
    /// <see cref="File.Exists"/>，成本远低于一次失败的进程启动。
    /// </para>
    /// <para>
    /// <b>失败冷却只约束「进程启动」，不约束「探测」</b>：冷却期（10 秒）内调用方拿到的是
    /// 缓存的失败原因；冷却过后重试就会重新走到这里读磁盘。
    /// </para>
    /// <para>
    /// <b>候选清单必须分平台</b>：<see cref="Environment.SpecialFolder.ProgramFiles"/> 在
    /// Linux / macOS 上返回空串，早期只写 Windows 路径的实现会让这些平台上的候选集合为空 ——
    /// 表现为「装了 Chromium 也永远报未找到」，而不是任何提示。
    /// </para>
    /// </remarks>
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

        return new BrowserExecutable(null, "未找到可用的 Chrome / Chromium / Edge");
    }

    /// <summary>候选路径（按优先级，分平台）。抽成静态方法以便单测钉住顺序。</summary>
    internal static IEnumerable<(string Path, string Source)> Candidates()
        => Candidates(CurrentPlatform());

    /// <summary>当前运行平台。</summary>
    private static BrowserPlatform CurrentPlatform()
        => OperatingSystem.IsWindows() ? BrowserPlatform.Windows
            : OperatingSystem.IsMacOS() ? BrowserPlatform.MacOS
            : BrowserPlatform.Linux;

    /// <summary>
    /// 指定平台的候选路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要把平台做成参数</b>：Linux 的候选清单是本次修复的主体
    /// （Ubuntu 服务器上「装了 Chromium 却永远探测不到」）。若只能在 Linux 上执行测试，
    /// 开发机（Windows）跑不到这条分支，漏项与顺序错误都不会被发现。
    /// 抽成显式平台参数后，三种清单都能在任何平台上被钉住。
    /// </para>
    /// <para>
    /// <b>为什么只在「平台 = 当前平台」时才读 PATH 与用户目录</b>：
    /// 在 Windows 上生成 Linux 清单时若读 Windows 的 <c>PATH</c>，
    /// 会得出「Linux 候选里混进 <c>C:\...</c>」这种自相矛盾的结果 ——
    /// 依赖「那些文件恰好不存在」才不出错，是纯粹的运气，不是正确性。
    /// </para>
    /// </remarks>
    internal static IEnumerable<(string Path, string Source)> Candidates(BrowserPlatform platform)
        => Candidates(platform, ResolveEnvironment(platform));

    /// <summary>单测注入用：显式给出 PATH 与用户主目录（<see langword="null"/> 表示不扫）。</summary>
    internal static IEnumerable<(string Path, string Source)> Candidates(
        BrowserPlatform platform,
        (string? PathVariable, string? HomeDirectory) environment)
    {
        var candidates = platform switch
        {
            BrowserPlatform.Windows => WindowsCandidates(),
            BrowserPlatform.MacOS => MacCandidates(),
            _ => LinuxCandidates(environment)
        };

        // 去重：固定路径、PATH 扫描、下载缓存三者会重叠（例如 /usr/bin/chromium
        // 既在固定清单里又在 PATH 里），重复项只会让失败文案里的候选变长。
        var seen = new HashSet<string>(platform == BrowserPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (path, source) in candidates)
        {
            if (seen.Add(path))
                yield return (path, source);
        }
    }

    /// <summary>取指定平台的环境（仅当它就是当前平台时才读真实环境变量）。</summary>
    private static (string? PathVariable, string? HomeDirectory) ResolveEnvironment(BrowserPlatform platform)
    {
        if (platform != CurrentPlatform())
            return (null, null);

        return (
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Windows 候选：Edge 优先（系统预装，零下载成本），其次 Chrome 与用户级安装。</summary>
    private static IEnumerable<(string Path, string Source)> WindowsCandidates()
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

        // ③ 用户级安装（Chrome 非管理员安装；PuppeteerSharp 下载到用户目录时）
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return (Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"), "用户级 Chrome");
            yield return (Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"), "用户级 Edge");
            yield return (Path.Combine(localAppData, "Chromium", "Application", "chrome.exe"), "用户级 Chromium");
        }
    }

    /// <summary>macOS 候选：应用包内的可执行文件。</summary>
    private static IEnumerable<(string Path, string Source)> MacCandidates()
    {
        yield return ("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "系统 Chrome");
        yield return ("/Applications/Chromium.app/Contents/MacOS/Chromium", "系统 Chromium");
        yield return ("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge", "系统 Edge");
    }

    /// <summary>
    /// Linux 候选：发行版包管理器安装位置 → PATH 扫描 → 下载缓存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能只列固定路径</b>：Ubuntu/Debian 用 <c>chromium</c> 还是
    /// <c>chromium-browser</c>（旧包名 / 过渡包）、Snap 装在 <c>/snap/bin</c>、
    /// 官方 deb 装到 <c>/opt/google/chrome</c> —— 组合太多，漏一个就等于「装了也探测不到」。
    /// </para>
    /// <para>
    /// <b>为什么还要扫 PATH</b>：用户手动解压、<c>~/.local/bin</c>、conda 环境等位置不在任何
    /// 标准目录里，但一定在 PATH 上。扫 PATH 只需读环境变量 + <see cref="File.Exists"/>，不起进程
    /// （不用 <c>which</c>）。
    /// </para>
    /// <para>
    /// <b>为什么扫 Puppeteer 缓存</b>：用户可能先按 Puppeteer 文档跑过
    /// <c>BrowserFetcher.DownloadAsync()</c>，浏览器就在 <c>~/.cache/puppeteer</c> 里，
    /// 此时不需要再装系统包。
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Path, string Source)> LinuxCandidates(
        (string? PathVariable, string? HomeDirectory) environment)
    {
        // ① 发行版包管理器 / Snap / 官方 deb 的固定位置
        yield return ("/usr/bin/google-chrome-stable", "系统 Chrome（stable）");
        yield return ("/usr/bin/google-chrome", "系统 Chrome");
        yield return ("/opt/google/chrome/chrome", "系统 Chrome（/opt 安装）");
        yield return ("/usr/bin/chromium", "系统 Chromium");
        yield return ("/usr/bin/chromium-browser", "系统 Chromium（旧包名）");
        yield return ("/snap/bin/chromium", "Snap Chromium");
        yield return ("/usr/bin/microsoft-edge-stable", "系统 Edge");
        yield return ("/usr/bin/microsoft-edge", "系统 Edge（beta/dev）");

        // ② PATH 兜底（~/.local/bin、手动解压、conda 等）
        foreach (var candidate in PathCandidates(environment.PathVariable))
            yield return candidate;

        // ③ PuppeteerSharp 下载缓存
        foreach (var candidate in DownloadedCandidates(environment.HomeDirectory))
            yield return candidate;
    }

    /// <summary>Linux 上要扫 PATH 的可执行文件名（与固定清单同名的命令形式）。</summary>
    private static readonly string[] s_linuxCommandNames =
    [
        "google-chrome-stable",
        "google-chrome",
        "chromium",
        "chromium-browser",
        "microsoft-edge-stable",
        "microsoft-edge"
    ];

    /// <summary>扫 PATH 目录寻找已知浏览器命令（不启动 <c>which</c> 子进程）。</summary>
    private static IEnumerable<(string Path, string Source)> PathCandidates(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            yield break;

        var seenDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seenDirs.Add(dir))
                continue;

            foreach (var name in s_linuxCommandNames)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full))
                    yield return (full, $"PATH 中的 {name}");
            }
        }
    }

    /// <summary>PuppeteerSharp / Puppeteer 下载缓存里的 chrome。</summary>
    /// <remarks>
    /// 两种目录结构都要认：新版 <c>~/.cache/puppeteer/chrome/linux-&lt;ver&gt;/chrome-linux64/chrome</c>，
    /// 旧版 PuppeteerSharp <c>~/.local-chromium/linux-&lt;rev&gt;/chrome-linux/chrome</c>。
    /// </remarks>
    private static IEnumerable<(string Path, string Source)> DownloadedCandidates(string? home)
    {
        if (string.IsNullOrEmpty(home))
            yield break;

        var newRoot = Path.Combine(home, ".cache", "puppeteer", "chrome");
        foreach (var path in EnumerateVersionedExecutables(newRoot, "chrome-linux64/chrome", "chrome-linux/chrome"))
            yield return (path, $"Puppeteer 缓存（{newRoot}）");

        var legacyRoot = Path.Combine(home, ".local-chromium");
        foreach (var path in EnumerateVersionedExecutables(legacyRoot, "chrome-linux/chrome"))
            yield return (path, $"Puppeteer 旧版缓存（{legacyRoot}）");
    }

    /// <summary>在「版本目录 / 相对可执行文件」两层结构里找可执行文件，版本新的优先。</summary>
    private static IEnumerable<string> EnumerateVersionedExecutables(string root, params string[] relativeLeafs)
    {
        var versionDirs = SafeGetDirectories(root);

        // 目录名降序：用户刚下载的那版排在最前（linux-131 &gt; linux-120）
        Array.Sort(versionDirs, (a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));

        foreach (var dir in versionDirs)
        {
            foreach (var leaf in relativeLeafs)
            {
                var full = Path.Combine(dir, leaf.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    yield return full;
            }
        }
    }

    /// <summary>读子目录列表；不可读（不存在 / 无权限）时返回空数组而不是抛。</summary>
    /// <remarks>
    /// 探测过程绝不能因为某个候选目录不可读就整体失败 ——
    /// 那会把「PATH 里有个无权限目录」变成「浏览器完全不可用」。
    /// </remarks>
    private static string[] SafeGetDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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
            // 没有系统浏览器：明确失败，不静默去下一步载几百 MB。
            // 用专用异常类型而不是普通 BrowserUnavailableException：
            // 上层（GetBrowserAsync）要据此判断「这是还没装，装完就该立刻重试」，
            // 而不是解析错误字符串（文案一改就失效）。
            throw new BrowserExecutableMissingException(
                $"{executable.Source}。{InstallHint()}" +
                $"或在 seeing.json 的 {BrowserOptions.SectionName}.ExecutablePath 指定浏览器路径");
        }

        // 有头模式要求有图形会话。Linux 服务器（无 DISPLAY）上 Chromium 会直接退出，
        // 错误只有一句 "Missing X server or $DISPLAY"；在这里提前拦下并给出可执行方案，
        // 比让用户对着「启动浏览器失败」猜要省事。
        //
        // 刻意**不**静默回落无头：用户开有头就是为了反检测，悄悄换成无头会让
        // 「设置里明明关了无头，行为却没变」这种更难查的问题出现。
        if (!options.Headless && MissingDisplay())
        {
            throw new BrowserUnavailableException(
                "有头模式需要图形会话，但当前环境没有 DISPLAY / WAYLAND_DISPLAY。" +
                "三种做法：① 在设置页打开「无头模式」；" +
                "② 用虚拟显示启动服务：xvfb-run -a dotnet run --project samples/Seeing.Agent.WebUI；" +
                "③ 用 SSH X11 转发并设置 DISPLAY。");
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

            // 先尽量清 PID 文件里的残留；杀不到（PID 已删、进程正在退）也不能立刻放弃——
            // CloseAsync 返回后子进程还可能握着锁几秒。旧逻辑 if (killed==0) throw
            // 会把「锁马上会放」做成永久性失败。
            // 注意：健康的「切会话再开」应复用同一进程（见 BrowserEvidenceProbes），
            // 不应走到这里；本分支只服务断连/关停后的二次 Launch。
            await KillLeftoverProcessAsync(profileDirectory).ConfigureAwait(false);

            // 等 profile 锁释放：条件轮询而不是固定等待
            browser = await RetryLaunchAfterCleanupAsync(launchOptions, profileDirectory, ex, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessException ex) when (TryExplainLaunchFailure(ex, out var explanation))
        {
            // 其余 ProcessException：Linux 上最常见的是缺少 Chromium 的运行时共享库
            // （libnss3 / libatk / libgbm …）或图形会话缺失。
            // 原始异常只有一句 "Failed to launch browser!" + 一段 stderr，
            // 用户看不出「该 apt install 什么」。这里把它翻成可执行的指引。
            _logger.LogWarning(ex, "启动浏览器失败：{Reason}", explanation);
            throw new BrowserUnavailableException(explanation!);
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

    /// <summary>
    /// 把启动失败的原始异常翻成「用户能照着做」的说明；返回 false 表示不识别（保留原始异常）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么值得做</b>：Linux 上 Chromium 起不来的绝大多数原因是缺少运行时依赖库，
    /// Puppeteer 只会抛出一段夹着 <c>error while loading shared libraries: libnss3.so</c>
    /// 的 stderr。用户看到的是「启动浏览器失败」五个字，实际需要的是
    /// <c>sudo apt install libnss3 ...</c>。识别不了的仍然原样抛出，不吞信息。
    /// </para>
    /// <para>
    /// <b>为什么只做模式匹配而不是解析完整 stderr</b>：Chromium 的报错文本随版本变化，
    /// 过度解析反而脆弱。这里只识别几个稳定特征串，命中就给指引，否则老老实实上报。
    /// </para>
    /// </remarks>
    internal static bool TryExplainLaunchFailure(ProcessException ex, out string? explanation)
    {
        // 同时看消息与内层异常：PuppeteerSharp 有时把 stderr 放在 InnerException，
        // 只匹配外层文案会漏掉真正的病因。
        return TryExplainLaunchFailure(
            string.Concat(ex.Message, "\n", ex.InnerException?.Message),
            out explanation);
    }

    /// <summary>按原始文本判定失败病因（抽出来是为了能在不构造 <see cref="ProcessException"/> 的前提下测试）。</summary>
    internal static bool TryExplainLaunchFailure(string message, out string? explanation)
    {
        // ① 缺少共享库（最典型：精简镜像 / 最小化 Ubuntu 服务器）
        if (message.Contains("shared libraries", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot open shared object file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error while loading shared libraries", StringComparison.OrdinalIgnoreCase))
        {
            var missing = ExtractMissingLibrary(message);
            explanation =
                $"浏览器可执行文件存在但缺少运行时依赖库{(string.IsNullOrEmpty(missing) ? string.Empty : $"（{missing}）")}。" +
                "Ubuntu/Debian 上安装 Chromium 的完整依赖：" +
                "sudo apt install -y libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 " +
                "libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 libgbm1 libasound2。" +
                "（或直接 sudo apt install -y chromium 让 apt 自动拉齐依赖）";
            return true;
        }

        // ② 没有图形会话（有头模式）
        if (message.Contains("Missing X server", StringComparison.OrdinalIgnoreCase)
            || message.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            explanation =
                "有头模式需要图形会话，但 Chromium 找不到 X server。" +
                "改用无头模式（设置页「无头模式」），或用 xvfb-run 启动服务。";
            return true;
        }

        // ③ 沙箱相关（容器 / 特权受限环境）
        if (message.Contains("No usable sandbox", StringComparison.OrdinalIgnoreCase)
            || message.Contains("namespace sandbox", StringComparison.OrdinalIgnoreCase))
        {
            explanation =
                "Chromium 沙箱在该环境不可用（常见于 Docker / 受限容器）。" +
                "在设置页「追加启动参数」里加 --no-sandbox 与 --disable-setuid-sandbox。";
            return true;
        }

        explanation = null;
        return false;
    }

    /// <summary>从 stderr 文本里抠出缺失的库名（用于让指引更具体）。</summary>
    private static string? ExtractMissingLibrary(string message)
    {
        const string marker = "error while loading shared libraries:";
        var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var rest = message[(index + marker.Length)..].TrimStart();
        var end = rest.IndexOf(':');
        return end > 0 ? rest[..end].Trim() : rest.Split(' ', '\n', '\r')[0];
    }

    /// <summary>判定异常是否为「profile 已被另一浏览器进程占用」。</summary>
    internal static bool IsProfileLocked(Exception ex)
        => ex is ProcessException && IsProfileLockFailure(ex.Message);

    /// <summary>文案/异常是否在描述 profile 锁（可恢复，应跳过失败冷却）。</summary>
    internal static bool IsProfileLockFailure(Exception ex)
        => IsProfileLockFailure(ex.Message);

    /// <summary>文案是否在描述 profile 锁。</summary>
    internal static bool IsProfileLockFailure(string? message)
        => !string.IsNullOrEmpty(message)
           && message.Contains("already running", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipFailureCooldown(BrowserUnavailableException ex)
        => ex is BrowserExecutableMissingException || IsProfileLockFailure(ex);

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
            // 这几条与指纹无关，纯稳定性：
            // ① /dev/shm 太小（容器里常见 64MB）会让渲染进程直接崩溃；
            // ② 首次运行向导 / 默认浏览器检查会弹窗或阻塞启动；
            // ③ 后台节流会让页面在「看不见」时被降频，表现为工具等待超时。
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

            // 注意：反检测档**不主动**加 --no-sandbox（它会被检测）。
            // 但 root 用户例外，见下方 MustDisableSandbox 的说明。
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

        // 反检测档下的沙箱例外：以 root 运行时 Chromium **硬拒绝**启动
        // （"Running as root without --no-sandbox is not supported"）。
        // 这不是「指纹更干净」与「能跑」的权衡 —— 不加就完全起不来，
        // 所以必须无条件补上，否则 Ubuntu 服务器（通常以 root 或 sudo 运行）上
        // 反检测模式永远无法使用，而错误信息只会说「启动失败」。
        if (options.AntiDetection && MustDisableSandbox())
            args.Add("--no-sandbox");

        // 用户追加参数（放最后，可覆盖前面的默认值）
        if (options.ExtraArgs is { Length: > 0 } extra)
            args.AddRange(extra);

        return [.. args];
    }

    /// <summary>是否缺少图形会话（仅 POSIX 有头模式相关）。</summary>
    /// <remarks>
    /// <para>
    /// 判据：既没有 <c>DISPLAY</c>（X11）也没有 <c>WAYLAND_DISPLAY</c>。
    /// 两者都空时 Chromium 起窗必失败（<c>Missing X server or $DISPLAY</c>）。
    /// </para>
    /// <para>
    /// <b>为什么 Windows 恒为 false</b>：Windows 没有这套环境变量，
    /// 且即使无交互桌面（服务账户）Puppeteer 也能用桌面会话启动窗口，
    /// 按环境变量判断会大量误杀。
    /// </para>
    /// </remarks>
    internal static bool MissingDisplay()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return false;

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
               && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    /// <summary>是否必须以 root 身份禁用沙箱（仅 POSIX 平台有意义）。</summary>
    /// <remarks>
    /// <para>
    /// <b>为什么只判用户名而不 P/Invoke <c>geteuid</c></b>：探测路径上多一次本机系统调用
    /// 不值得，而「UID 为 0」在 .NET 里没有跨平台 API。用户名 <c>root</c> 覆盖了
    /// Docker 默认用户、<c>sudo</c> 后、云镜像默认登录用户这几种实际场景。
    /// </para>
    /// <para>
    /// <b>误判的代价是可控的</b>：把非 root 的 <c>root</c> 用户也加上 <c>--no-sandbox</c>，
    /// 结果是反检测弱化一点点，但浏览器仍能正常启动 —— 远好过相反方向的「完全起不来」。
    /// </para>
    /// </remarks>
    internal static bool MustDisableSandbox()
        => !OperatingSystem.IsWindows()
           && string.Equals(Environment.UserName, "root", StringComparison.Ordinal);

    /// <summary>按平台给出「怎么装浏览器」的可执行指引（失败文案的一部分）。</summary>
    /// <remarks>
    /// <b>为什么按平台分</b>：早期文案只写「请安装 Microsoft Edge 或 Google Chrome」，
    /// 在 Ubuntu 上这句话没有给出任何可执行的命令（Edge 无官方 deb 源，
    /// <c>apt install chrome</c> 也不存在），用户只能自己去猜 —— 属于「报错但不帮忙」。
    /// </remarks>
    internal static string InstallHint()
        => OperatingSystem.IsWindows()
            ? "请安装 Microsoft Edge 或 Google Chrome，"
            : OperatingSystem.IsMacOS()
                ? "请安装 Google Chrome / Chromium / Microsoft Edge，"
                : "请安装 Chromium（如 sudo apt install chromium-browser）或 Google Chrome，";

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

    private void Remember(string reason, bool skipCooldown = false)
    {
        _failedAt = DateTimeOffset.UtcNow;
        _failureReason = reason;
        _skipFailureCooldown = skipCooldown;
    }

    private async Task DisposeBrowserQuietlyAsync()
    {
        if (_browser is null)
            return;

        var browser = _browser;
        var profileDirectory = _launchedProfileDirectory;
        var process = browser.Process;
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

        // PID 文件只在主进程确认退出后删：CloseAsync 返回时子进程还可能握着 profile 锁。
        // 此时若删掉 PID，下次撞锁 KillLeftover 找不到目标 → 「被另一个会话占用」假象。
        if (profileDirectory is null)
            return;

        var pidPath = Path.Combine(profileDirectory, PidFileName);
        try
        {
            if (process is { HasExited: false })
            {
                try
                {
                    await process.WaitForExitAsync(
                            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug(
                        "关闭后浏览器进程 {Pid} 仍未退出，保留 PID 文件供下次清理",
                        process.Id);
                }
            }

            if (process is null || process.HasExited)
                File.Delete(pidPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理浏览器 PID 记录失败（忽略）");
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
            _skipFailureCooldown = false;
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
public class BrowserUnavailableException : Exception
{
    /// <summary>创建异常。</summary>
    public BrowserUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// 磁盘上找不到浏览器可执行文件（还没装，或装在了非探测位置）。
/// </summary>
/// <remarks>
/// <b>为什么单独一个类型</b>：这是唯一一种「用户修好之后应当<b>立刻</b>可用」的失败。
/// 服务启动时浏览器还没装、运行中才装上，是服务器部署的正常顺序——
/// 让用户为此等满 10 秒失败冷却，会让人以为「装了也没用」。
/// 用异常类型区分，比在失败路径上匹配错误文案可靠（文案会改）。
/// </remarks>
public sealed class BrowserExecutableMissingException : BrowserUnavailableException
{
    /// <summary>创建异常。</summary>
    public BrowserExecutableMissingException(string message) : base(message)
    {
    }
}
