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
/// 浏览器客户端连接器：发现共享宿主 → 探活 → <c>ConnectAsync</c>；失败则拉起宿主并重试。
/// </summary>
/// <remarks>
/// <para>
/// <b>客户端不再拥有浏览器进程</b>：一个 profile 只有一个宿主进程持有 Chromium（见
/// <c>BrowserProcessHost</c>），任意多个客户端（桌面 / TUI / WebUI 多窗口）连同一个 CDP endpoint。
/// 客户端各自建页、各自断开，谁退出都不该带走别人的页面与登录态。
/// </para>
/// <para>
/// <b>唯一释放通道是 <see cref="IBrowser.Disconnect"/></b>：PuppeteerSharp 没有
/// <c>DisconnectAsync</c>，而 <c>CloseAsync</c> / <c>DisposeAsync</c> 都会向共享浏览器发
/// <c>Browser.close</c> —— 一次客户端退出会杀掉所有人的 Chromium。因此本类**禁止**调用它们。
/// </para>
/// <para>
/// <b>为什么懒连接</b>：连接与拉起宿主都有成本（后者要启动 Chromium）。绝大多数会话根本不碰浏览器，
/// 上一次工具调用才连是唯一合理的时机。
/// </para>
/// <para>
/// <b>失败缓存</b>：连接失败后 10 秒内不再重试。否则模型连续调用几个 browser_* 工具会反复
/// 走满 15 秒拉起窗口，看起来像「界面卡死」。
/// </para>
/// <para>
/// <b>反检测分层</b>：浏览器级（UA 启动参数、指纹相关的启动开关）由宿主决定；
/// 客户端只对<b>自己创建的 page</b> 注入页面级脚本（见 <see cref="ApplyAntiDetectionToPageAsync"/>）。
/// 遍历 <c>PagesAsync()</c> 或挂 <c>TargetCreated</c> 会把伪装强加给别的客户端正在用的页面。
/// </para>
/// </remarks>
public sealed class BrowserEngine : IAsyncDisposable
{
    /// <summary>连接失败后的冷却时长。</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(10);

    /// <summary>TCP 探活上限（1 秒内连上并拿到 HTTP 响应才算宿主可用）。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>拉起宿主后等待它就绪的总预算。</summary>
    private static readonly TimeSpan HostStartupLimit = TimeSpan.FromSeconds(15);

    /// <summary>等待宿主就绪的轮询间隔（E5：轮询实现细节，命中即返回）。</summary>
    private static readonly TimeSpan HostStartupPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>一轮连接流程里最多拉起几次宿主（防止「拉起就崩」变成进程风暴）。</summary>
    private const int MaxHostLaunches = 3;

    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly ILogger<BrowserEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _baseDirectoryOverride;

    private IBrowser? _browser;

    private DateTimeOffset? _failedAt;
    private string? _failureReason;

    /// <summary>创建引擎。</summary>
    /// <param name="options">配置（热重载：每次连接读当前值）。</param>
    /// <param name="logger">日志。</param>
    public BrowserEngine(IOptionsMonitor<BrowserOptions> options, ILogger<BrowserEngine>? logger = null)
        : this(options, logger, baseDirectoryOverride: null)
    {
    }

    /// <summary>创建引擎，并显式指定发现文件 / 客户端锁根目录（仅测试与诊断用）。</summary>
    /// <param name="options">配置。</param>
    /// <param name="logger">日志。</param>
    /// <param name="baseDirectoryOverride">
    /// 发现文件根目录；<see langword="null"/> = 环境变量 <c>SEEING_BROWSER_BASE_DIR</c>，再回落
    /// <c>~/.seeing/browser</c>。生产代码走公开构造函数。
    /// </param>
    internal BrowserEngine(
        IOptionsMonitor<BrowserOptions> options,
        ILogger<BrowserEngine>? logger,
        string? baseDirectoryOverride)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<BrowserEngine>.Instance;
        _baseDirectoryOverride = baseDirectoryOverride;
    }

    /// <summary>当前是否已连上共享宿主。</summary>
    public bool IsStarted => _browser is { IsConnected: true };

    /// <summary>
    /// 本客户端连接的宿主 profile 目录（绝对路径）。
    /// </summary>
    /// <remarks>
    /// 必须与宿主用同一个纯函数解析：两边各自现算会得到两个身份，客户端永远找不到正在跑的宿主，
    /// 每次都再拉起一个（见 <see cref="BrowserHostLocator.ResolveHostProfileDirectory"/>）。
    /// </remarks>
    internal string HostProfileDirectory
        => BrowserHostLocator.ResolveHostProfileDirectory(_options.CurrentValue.ProfileDirectory);

    /// <summary>
    /// 发现文件 / 客户端锁的根目录（<see cref="BrowserHostLocator.BaseDirectory"/>）。
    /// </summary>
    /// <remarks>
    /// <see cref="BrowserSessionHost"/> 建 presence 时必须用同一份目录，否则宿主扫客户端存活时
    /// 看不到本进程，会把它判成「没有客户端」并空闲退出。
    /// 目录解析只在 <see cref="BrowserHostLocator.ResolveBaseDirectory"/> 一处实现（含
    /// <see cref="BrowserHostLocator.BaseDirectoryVariable"/> 覆盖），这里不再复制一份。
    /// </remarks>
    internal string DiscoveryBaseDirectory
        => BrowserHostLocator.ResolveBaseDirectory(_baseDirectoryOverride);

    /// <summary>
    /// 最近一次连接使用的 CDP endpoint（测试与诊断用；未连接时为 <see langword="null"/>）。
    /// </summary>
    internal string? CurrentEndpointForTest { get; private set; }

    /// <summary>
    /// 取（必要时连接并拉起宿主）共享浏览器。
    /// </summary>
    /// <exception cref="BrowserHostVersionMismatchException">发现文件协议版本与当前实现不一致。</exception>
    /// <exception cref="BrowserUnavailableException">宿主无法就绪，或浏览器连不上。</exception>
    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (_browser is { IsConnected: true } alive)
            return alive;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true } again)
                return again;

            // 失败冷却：短期内不再重连，直接复用上次的原因。
            // 版本不匹配**不**进冷却（见下面的专用异常路径）：它只能靠重启宿主解决，
            // 用户重启后应当立刻可用，而不是再等 10 秒。
            if (_failedAt is { } failedAt && DateTimeOffset.UtcNow - failedAt < FailureCooldown)
                throw new BrowserUnavailableException(_failureReason ?? "浏览器宿主不可用");

            try
            {
                var browser = await ConnectOrStartAsync(cancellationToken).ConfigureAwait(false);
                _browser = browser;
                _failedAt = null;
                _failureReason = null;
                return browser;
            }
            catch (BrowserHostVersionMismatchException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BrowserUnavailableException ex)
            {
                Remember(ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                var message = $"连接浏览器宿主失败：{ex.Message}";
                Remember(message);
                _logger.LogWarning(ex, "连接浏览器宿主失败");
                throw new BrowserUnavailableException(message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 连接已有宿主；没有可用宿主则拉起，并在 15 秒内每 500ms 重试一次完整流程。
    /// </summary>
    /// <remarks>
    /// <b>每轮都重新读发现文件</b>：旧宿主正在退出时，发现文件可能仍是旧的（端点已失效），
    /// 而新宿主稍后才覆盖它。只读一次会一直用那个过期端点，永远连不上。
    /// </remarks>
    private async Task<IBrowser> ConnectOrStartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var profile = HostProfileDirectory;
        var locator = new BrowserHostLocator(profile, DiscoveryBaseDirectory, _logger);

        // ① 已有宿主：读发现 → 版本 → 探活 → ConnectAsync
        var info = locator.Read();
        ThrowIfVersionMismatch(locator, info);

        if (info is not null
            && await TryConnectAsync(locator, info, options, cancellationToken).ConfigureAwait(false) is { } connected)
        {
            return connected;
        }

        // ② 没有可用宿主：拉起（宿主级配置必须传过去，否则自定义 Edge 路径 / 无头开关全部失效）
        var hostProcess = StartHostProcess(profile, options);
        var launches = 1;
        var deadline = DateTimeOffset.UtcNow + HostStartupLimit;

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                // E5：轮询间隔（命中即返回，不是「等状态就绪」的固定等待）。
                await Task.Delay(HostStartupPollInterval, cancellationToken).ConfigureAwait(false);

                info = locator.Read();
                ThrowIfVersionMismatch(locator, info);

                if (info is not null
                    && await TryConnectAsync(locator, info, options, cancellationToken).ConfigureAwait(false) is { } browser)
                {
                    return browser;
                }

                // 拉起的宿主已经退出且发现文件仍不可用：再拉一次（覆盖「旧宿主退出、锁刚释放」的窗口）。
                // ⚠ 退出码**不**作为成功判据：Windows Job 的 KILL_ON_JOB_CLOSE 会让崩溃也表现为 exit 0。
                // 这里只把「进程没了」当作「值得再拉一次」的信号，成功与否始终由发现文件 + 探活决定。
                // 有上限，避免宿主反复崩溃时把机器拖死。
                if (launches < MaxHostLaunches && (hostProcess is null || HasExitedQuietly(hostProcess)))
                {
                    hostProcess?.Dispose();
                    hostProcess = StartHostProcess(profile, options);
                    launches++;
                }
            }
        }
        finally
        {
            // 只释放本进程的 Process 句柄，**不杀**子进程：宿主是共享的，可能已被其它客户端连上。
            hostProcess?.Dispose();
        }

        throw new BrowserUnavailableException(
            $"浏览器宿主未在 {HostStartupLimit.TotalSeconds:0} 秒内就绪（profile：{profile}）。" +
            $"宿主日志：{Path.Combine(locator.BaseDirectory, $"host-{locator.ProfileHash}.log")}。");
    }

    /// <summary>发现文件版本不匹配时抛出专用异常（提示用户重启浏览器宿主）。</summary>
    private void ThrowIfVersionMismatch(BrowserHostLocator locator, BrowserHostInfo? info)
    {
        if (info is null || locator.VersionMatches(info))
            return;

        throw new BrowserHostVersionMismatchException(
            $"运行中的浏览器宿主协议版本为 {info.Version}，当前实现需要 {BrowserHostLocator.CurrentVersion}。" +
            "请重启浏览器宿主（关闭宿主进程后下次使用浏览器会自动重启），或稍后重试。");
    }

    /// <summary>探活 → 连接 → 复核宿主身份；任何一步失败返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// <b>连接后必须复核发现文件</b>：探活与连接之间，旧宿主可能已退出、新宿主已覆盖发现文件。
    /// 不对 PID + 启动时刻做一次 <see cref="BrowserHostLocator.IsSameHost"/> 复核，
    /// 客户端会一直连在一个「已经被替换掉」的宿主上（下一次调用才发现端点失效）。
    /// </remarks>
    private async Task<IBrowser?> TryConnectAsync(
        BrowserHostLocator locator,
        BrowserHostInfo info,
        BrowserOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(info.Endpoint))
            return null;

        if (!await ProbeEndpointAsync(info.Endpoint, ProbeTimeout, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("浏览器宿主探活失败（endpoint={Endpoint}），按「宿主不可用」处理", info.Endpoint);
            return null;
        }

        IBrowser browser;
        try
        {
            browser = await Puppeteer.ConnectAsync(
                new ConnectOptions { BrowserWSEndpoint = info.Endpoint }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 探活成功但连接失败：宿主的 CDP 可能刚好在关闭，交给下一轮重试。
            _logger.LogDebug(ex, "连接浏览器宿主失败（endpoint={Endpoint}）", info.Endpoint);
            return null;
        }

        // 复核：发现文件必须仍然描述「我们刚连上的这个宿主」。
        var current = locator.Read();
        if (!BrowserHostLocator.IsSameHost(current, info.HostPid, info.HostStartedAt)
            || !string.Equals(current!.Generation, info.Generation, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "连接期间宿主已更换（读到 {CurrentPid}/{CurrentGeneration}，连接的是 {Pid}/{Generation}），重新连接",
                current?.HostPid ?? 0, current?.Generation ?? "-", info.HostPid, info.Generation);

            // 只断开，绝不能关掉共享浏览器（它现在属于新宿主）。
            DisconnectQuietly(browser);
            return null;
        }

        _logger.LogInformation(
            "已连接浏览器宿主（generation={Generation}，endpoint={Endpoint}，profile={Profile}）",
            info.Generation, info.Endpoint, info.ProfileDirectory);

        browser.Disconnected += OnBrowserDisconnected;
        CurrentEndpointForTest = info.Endpoint;

        if (ShouldRelaunch(info, options))
        {
            // 只提示，不自动重启：用户可能正在页面里操作，换掉宿主等于把人家的窗口关掉。
            _logger.LogWarning(
                "浏览器宿主正在使用的设置与本机配置不一致；改动需重启浏览器宿主后生效（profile={Profile}）",
                info.ProfileDirectory);
        }

        return browser;
    }

    /// <summary>
    /// 短 TCP 探活：连上 endpoint 的端口并确认它能回一个 HTTP 响应（1 秒内）。
    /// </summary>
    /// <remarks>
    /// <b>为什么不用 <c>HttpClient</c></b>：这里只要「端口活着且是 Chromium 的 HTTP 端点」，
    /// 一个 socket + 一次 GET 足够；<c>HttpClient</c> 的实例生命周期管理对一次探活是纯负担。
    /// <b>为什么先探活再 <c>ConnectAsync</c></b>：发现文件可能属于刚崩溃的宿主，
    /// 直接连会抛一个看不出原因的 WebSocket 异常；先探活能把「端点失效」变成可预期的重试。
    /// </remarks>
    internal static async Task<bool> ProbeEndpointAsync(
        string endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Scheme is not ("ws" or "wss" or "http" or "https"))
        {
            return false;
        }

        var port = uri.IsDefaultPort
            ? (uri.Scheme is "ws" or "http" ? 80 : 443)
            : uri.Port;

        using var client = new System.Net.Sockets.TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(uri.Host, port, timeoutCts.Token).ConfigureAwait(false);

            var request = System.Text.Encoding.ASCII.GetBytes(
                $"GET /json/version HTTP/1.1\r\nHost: {uri.Host}:{port}\r\nConnection: close\r\n\r\n");

            using var stream = client.GetStream();
            await stream.WriteAsync(request, timeoutCts.Token).ConfigureAwait(false);

            var buffer = new byte[5];
            var read = await stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            return read == buffer.Length
                   && System.Text.Encoding.ASCII.GetString(buffer).StartsWith("HTTP/", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：必须抛出，不能伪装成「宿主不可用」而进入重试循环
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                       or System.Net.Sockets.SocketException
                                       or IOException
                                       or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 拼接「启动宿主」的命令：优先本进程同目录的 apphost，否则 <c>dotnet &lt;entry.dll&gt;</c>。
    /// </summary>
    /// <param name="profileDirectory">宿主 profile 目录（绝对路径）。</param>
    /// <param name="options">客户端配置（宿主级项会转成命令行参数）。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么不能只看 <see cref="Environment.ProcessPath"/></b>：<c>dotnet run</c> / <c>vstest</c>
    /// 下它是 <c>dotnet.exe</c> / <c>testhost.exe</c>，直接拿它加 <c>--browser-host</c> 会被当成子命令报错。
    /// 那种场景必须显式给出入口 dll。
    /// </para>
    /// <para>
    /// <b>宿主级配置必须通过命令行传</b>：宿主进程读的是命令行（没有客户端的 seeing.json 上下文）。
    /// 少传一项，用户的「自定义 Edge 路径 / 有头模式 / 追加参数」就在共享模式下静默失效。
    /// </para>
    /// </remarks>
    internal static (string FileName, string[] PrefixArgs) ResolveHostCommand(
        string profileDirectory,
        BrowserOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        var arguments = new List<string>();

        var appHostPath = entryName is null
            ? null
            : Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? entryName + ".exe" : entryName);

        var processPath = Environment.ProcessPath;
        var isDotnetMuxer = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

        string fileName;
        if (!isDotnetMuxer && appHostPath is not null && File.Exists(appHostPath))
        {
            fileName = appHostPath;
        }
        else
        {
            fileName = string.IsNullOrWhiteSpace(processPath) ? "dotnet" : processPath!;

            var entryDll = entryName is null
                ? null
                : Path.Combine(AppContext.BaseDirectory, entryName + ".dll");

            if (entryDll is not null && File.Exists(entryDll))
                arguments.Add(entryDll);
        }

        arguments.Add(HostSwitchMarker);
        arguments.Add("--profile");
        arguments.Add(profileDirectory);

        // 与 BrowserHostRunner.ParseHostStartup 的参数表一一对应。
        arguments.Add("--headless");
        arguments.Add(options.Headless ? "true" : "false");

        // 反检测是浏览器级设置（启动参数 + UA），必须由宿主决定；
        // 客户端只做页面级注入。不传这一项，用户在设置页关掉反检测后在共享模式下毫无效果。
        // 参数名与宿主解析（BrowserHostRunner.ParseHostStartup 的 --anti-detection）严格一致。
        arguments.Add("--anti-detection");
        arguments.Add(options.AntiDetection ? "true" : "false");

        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            arguments.Add("--executable");
            arguments.Add(options.ExecutablePath);
        }

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
        {
            arguments.Add("--user-agent");
            arguments.Add(options.UserAgent);
        }

        if (options.ExtraArgs is { Length: > 0 } extraArgs)
        {
            foreach (var arg in extraArgs)
            {
                arguments.Add("--extra-arg");
                arguments.Add(arg);
            }
        }

        return (fileName, [.. arguments]);
    }

    /// <summary>宿主的模式开关（与 <c>BrowserHostRunner.BrowserHostSwitch</c> 同一字面量）。</summary>
    private const string HostSwitchMarker = "--browser-host";

    /// <summary>
    /// 宿主实际生效的设置与本机配置是否不一致（不一致只提示「需重启宿主」，不自动重启）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能自动重启</b>：重启宿主会关掉所有人的浏览器窗口与页面。
    /// 用户可能正在有头窗口里处理登录/验证码，为了「让一个配置项生效」而把窗口撤掉是不可接受的。
    /// </para>
    /// <para>
    /// <b>为什么 <c>ExecutablePath</c> 只在本地显式配置时才比较</b>：宿主总会把可执行文件解析成
    /// 绝对路径（自动探测时客户端配置是 <see langword="null"/>），那种「本地为空、宿主有值」
    /// 不是不一致。
    /// </para>
    /// </remarks>
    internal static bool ShouldRelaunch(BrowserHostInfo info, BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (info?.Effective is not { } effective)
            return false;

        if (effective.Headless != options.Headless)
            return true;

        if (effective.AntiDetection != options.AntiDetection)
            return true;

        if (!string.Equals(NormalizeText(effective.UserAgent), NormalizeText(options.UserAgent), StringComparison.Ordinal))
            return true;

        if (!SameArgs(effective.ExtraArgs, options.ExtraArgs))
            return true;

        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!string.Equals(
                    NormalizePath(options.ExecutablePath), NormalizePath(effective.ExecutablePath), comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 路径不可规范化（非法字符等）：按原串比较，至少不会把明显不同的路径判成相同
            return value;
        }
    }

    private static bool SameArgs(string[]? left, string[]? right)
    {
        var a = left ?? [];
        var b = right ?? [];

        if (a.Length != b.Length)
            return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>拉起宿主进程（不等待、不接管）：<c>CreateNoWindow</c>，与客户端脱离。</summary>
    /// <remarks>返回 <see langword="null"/> 表示启动失败（原因已记日志）；调用方仍会进入重试循环。</remarks>
    private System.Diagnostics.Process? StartHostProcess(string profileDirectory, BrowserOptions options)
    {
        var (fileName, prefixArgs) = ResolveHostCommand(profileDirectory, options);

        var startInfo = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // 发现文件根目录必须与客户端一致：测试用环境变量隔离，
            // 生产下就是默认的 ~/.seeing/browser（显式传一份，避免两边各算一次）。
            Environment = { [BrowserHostLocator.BaseDirectoryVariable] = DiscoveryBaseDirectory },
        };

        foreach (var arg in prefixArgs)
            startInfo.ArgumentList.Add(arg);

        try
        {
            var process = System.Diagnostics.Process.Start(startInfo);
            _logger.LogInformation(
                "已拉起浏览器宿主（{File}，profile={Profile}）", fileName, profileDirectory);
            return process;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or IOException)
        {
            _logger.LogWarning(ex, "拉起浏览器宿主失败：{File}", fileName);
            return null;
        }
    }

    private static bool HasExitedQuietly(System.Diagnostics.Process process)
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
    /// 对<b>本客户端自己创建的</b>页面应用 UA 与 stealth 脚本（<see cref="BrowserSessionHost"/> 建页后调用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么只接受单个 page</b>：浏览器是共享的，<c>PagesAsync()</c> 遍历或挂
    /// <c>TargetCreated</c> 会把伪装强加给别的客户端正在用的页面 —— 那是别人的会话，
    /// 本客户端没有权利改它的 UA 与注入脚本。
    /// </para>
    /// <para>
    /// <b>为什么必须在首次导航前调用</b>：<c>EvaluateFunctionOnNewDocumentAsync</c> 只对
    /// 之后创建的文档生效；页面已经导航过就漏掉首次指纹检测。幂等：重复设置同一 UA 与同一脚本无副作用。
    /// </para>
    /// </remarks>
    public async Task ApplyAntiDetectionToPageAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!_options.CurrentValue.AntiDetection && string.IsNullOrWhiteSpace(_options.CurrentValue.UserAgent))
            return;

        await ApplyToPageAsync(page, _options.CurrentValue, _browser).ConfigureAwait(false);
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

    /// <summary>
    /// 宿主断连：清缓存，下次调用重新连接（不存在「重启浏览器」这回事，浏览器归宿主）。
    /// </summary>
    private void OnBrowserDisconnected(object? sender, EventArgs e)
    {
        _logger.LogWarning("与浏览器宿主的连接已断开；下次调用将重新连接");

        // 只清「确实是当前这个连接」的引用：旧连接迟到的 Disconnected 事件可能在
        // GetBrowserAsync 已把新连接赋给 _browser 之后才到达，无条件置 null 会把健康的引用清掉。
        //
        // 用比较交换而不是取锁：本方法在 CDP 回调线程上执行，
        // 而 _gate 持有时会 await 浏览器操作——在那里等锁会与 CDP 线程互相等待。
        if (ReferenceEquals(Interlocked.CompareExchange(ref _browser, null, sender as IBrowser), sender))
            CurrentEndpointForTest = null;
    }

    private void Remember(string reason)
    {
        _failedAt = DateTimeOffset.UtcNow;
        _failureReason = reason;
    }

    /// <summary>
    /// 断开当前连接（唯一的释放通道）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只能 <see cref="IBrowser.Disconnect"/></b>：PuppeteerSharp 没有 <c>DisconnectAsync</c>，
    /// 而 <c>CloseAsync</c> / <c>DisposeAsync</c> 都会向共享浏览器发 <c>Browser.close</c> ——
    /// 一个客户端退出会关掉所有人的 Chromium（含别人的页面与登录态）。
    /// </para>
    /// <para>
    /// <b>为什么不 <c>DisposeAsync</c> 释放 <see cref="IBrowser"/></b>：它对连接对象而言等价于关闭浏览器，
    /// 见上一条。断开后直接丢掉引用，由 GC 收掉客户端侧的代理对象。
    /// </para>
    /// </remarks>
    private void DisconnectCurrent()
    {
        var browser = _browser;
        _browser = null;
        CurrentEndpointForTest = null;

        if (browser is not null)
            DisconnectQuietly(browser);
    }

    /// <summary>断开单个连接（不抛：清理路径不应因为连接已死而中断）。</summary>
    private void DisconnectQuietly(IBrowser browser)
    {
        browser.Disconnected -= OnBrowserDisconnected;

        try
        {
            browser.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "断开浏览器连接时出现异常（忽略）");
        }
    }

    /// <summary>
    /// 断开与共享宿主的连接，但保持引擎可再次使用（模块停用 / 热重载路径）。
    /// </summary>
    /// <remarks>
    /// <b>绝不关浏览器</b>：浏览器属于宿主进程，其它客户端可能正在使用。
    /// 与 <see cref="DisposeAsync"/> 的区别：这里<b>不释放</b>内部信号量。
    /// 模块停用后可能被重新启用，DI 单例仍是同一个实例；释放信号量会让后续调用永久失败。
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisconnectCurrent();
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
            DisconnectCurrent();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

/// <summary>没有可用浏览器 / 宿主不可用。</summary>
public class BrowserUnavailableException : Exception
{
    /// <summary>创建异常。</summary>
    public BrowserUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// 运行中的宿主协议版本与当前实现不一致（代码升级了，旧宿主还在跑）。
/// </summary>
/// <remarks>
/// <b>为什么单独一个类型</b>：这不是「浏览器坏了」，而是必须由用户/运维重启宿主才能解决的断代问题。
/// 调用方（设置页、工具）要据此给出「重启浏览器宿主」的指引，而不是「安装浏览器」之类的建议。
/// </remarks>
public sealed class BrowserHostVersionMismatchException : BrowserUnavailableException
{
    /// <summary>创建异常。</summary>
    public BrowserHostVersionMismatchException(string message) : base(message)
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
