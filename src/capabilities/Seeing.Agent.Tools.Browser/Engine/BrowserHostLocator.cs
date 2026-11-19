using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Files;
using Seeing.Serialization;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 宿主「实际生效值」快照。客户端拿自己的 <see cref="BrowserOptions"/> 与它比较，
/// 不一致时提示「修改需重启浏览器」，**不自动重启**（避免在用户操作中换掉浏览器）。
/// </summary>
/// <param name="Headless">是否无头。</param>
/// <param name="ExecutablePath">实际使用的浏览器可执行文件（<see langword="null"/> = 自动探测）。</param>
/// <param name="UserAgent">实际注入的 UA（<see langword="null"/> = 未指定）。</param>
/// <param name="AntiDetection">是否启用反检测。</param>
/// <param name="ExtraArgs">实际追加的启动参数。</param>
/// <param name="HostIdleSeconds">宿主空闲退出秒数。</param>
public sealed record BrowserHostEffective(
    bool Headless,
    string? ExecutablePath,
    string? UserAgent,
    bool AntiDetection,
    string[] ExtraArgs,
    int HostIdleSeconds);

/// <summary>
/// 宿主发现文件内容：一个 profile 当前（或最近一次）宿主进程的身份与接入点。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有 <see cref="Generation"/> 与 <see cref="HostStartedAt"/> 两个「身份」</b>：
/// 文件会被后继宿主覆盖，客户端也可能读到崩溃前的旧文件。PID 会被系统复用，
/// 只看 PID 会把「另一个进程恰好拿到同一个 PID」当成「还是那个宿主」；
/// 因此存活判定必须同时比对 PID 与启动时刻（见 <see cref="BrowserHostLocator.IsSameHost"/>）。
/// </para>
/// </remarks>
/// <param name="Version">协议断代版本（见 <see cref="BrowserHostLocator.CurrentVersion"/>）。</param>
/// <param name="Generation">本次宿主启动的短标识，用于日志与「已换过宿主」的判断。</param>
/// <param name="HostPid">宿主进程 PID。</param>
/// <param name="HostStartedAt">宿主进程启动时刻（与 PID 一起构成身份）。</param>
/// <param name="ProfileDirectory">宿主使用的 profile 目录（绝对路径）。</param>
/// <param name="BrowserPid">Chromium 进程 PID（尚未拉起时为 <see langword="null"/>）。</param>
/// <param name="BrowserStartedAt">Chromium 启动时刻。</param>
/// <param name="Endpoint">CDP WebSocket 接入点。</param>
/// <param name="LastError">宿主最近一次失败原因（正常为 <see langword="null"/>）。</param>
/// <param name="Effective">宿主实际生效配置。</param>
public sealed record BrowserHostInfo(
    int Version,
    string Generation,
    int HostPid,
    DateTimeOffset HostStartedAt,
    string ProfileDirectory,
    int? BrowserPid,
    DateTimeOffset? BrowserStartedAt,
    string Endpoint,
    string? LastError,
    BrowserHostEffective? Effective);

/// <summary>
/// 宿主发现文件的读写与身份判定（<c>~/.seeing/browser/host-&lt;profileHash&gt;.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么发现文件放在用户目录而不是 profile 目录</b>：客户端要在**拉起宿主之前**就知道
/// 「有没有宿主、接入点在哪」。此时 profile 目录可能还不存在（首次运行），
/// 而且 profile 目录本身在浏览器启动期间会被 Chromium 锁住一阵子。
/// </para>
/// <para>
/// <b>为什么 <c>profileHash</c> 基于绝对路径</b>：宿主始终使用固定 profile 目录
/// （与 <see cref="BrowserOptions.PersistLogin"/> 无关，见
/// <see cref="ResolveHostProfileDirectory"/>）。客户端与宿主只要拼出同一份绝对路径，
/// 就得到同一个 hash；若基于相对路径或配置项，同一目录的不同写法会算出两个身份，
/// 表现为「客户端看不到正在运行的宿主，于是又拉起一个」。
/// </para>
/// <para>
/// <b>为什么 <see cref="TryDelete"/> 要校验 PID</b>：宿主退出时会删发现文件，
/// 而它可能已经被后继宿主覆盖（旧宿主退出得比新宿主启动还晚）。不校验 PID 就会删掉
/// 后来者的文件，让所有客户端「看不见宿主」并重复拉起。
/// </para>
/// <para>
/// <b>发现文件读不出来时必须记为可观测事件</b>：文件损坏 / 半截 / 旧版本都会让客户端
/// 「看不见宿主」并重复拉起，症状只有「浏览器莫名多开一个」。静默 catch 会让这条路径
/// 完全无法排查，因此所有读盘失败都记 Debug（带异常）。
/// </para>
/// </remarks>
public sealed class BrowserHostLocator
{
    /// <summary>发现文件与客户端锁的根目录名（位于用户级 <c>.seeing</c> 下）。</summary>
    public const string RootDirectoryName = "browser";

    /// <summary>宿主固定 profile 目录名（位于用户级 <c>.seeing</c> 下）。</summary>
    public const string ProfileDirectoryName = "browser-profile";

    /// <summary>当前发现文件协议版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>发现文件名前缀。</summary>
    private const string InfoFilePrefix = "host-";

    /// <summary>发现文件名后缀。</summary>
    private const string InfoFileSuffix = ".json";

    /// <summary>
    /// 写盘：可读缩进 + camelCase（设计文档里的字段名）。
    /// <para>统一走 <see cref="SeeingJson"/>，禁止裸写 <c>new JsonSerializerOptions</c>。</para>
    /// </summary>
    private static readonly JsonSerializerOptions s_writeOptions = SeeingJson.Create(
        indented: true,
        options => options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

    /// <summary>读盘：兼容大小写，容忍手工编辑过的文件。</summary>
    private static readonly JsonSerializerOptions s_readOptions = SeeingJson.Create(
        indented: false,
        options =>
        {
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PropertyNameCaseInsensitive = true;
        });

    private readonly ILogger? _logger;

    /// <summary>创建发现文件定位器。</summary>
    /// <param name="profileDirectory">宿主 profile 目录（相对路径会按当前目录转成绝对路径）。</param>
    /// <param name="baseDirectory">
    /// 发现文件根目录；<see langword="null"/> = <c>~/.seeing/browser</c>。
    /// 显式传入只为测试隔离，生产代码一律用默认值。
    /// </param>
    /// <param name="logger">日志（可选；发现文件读不出来正是最需要线索的地方）。</param>
    public BrowserHostLocator(
        string profileDirectory,
        string? baseDirectory = null,
        ILogger? logger = null)
    {
        ProfileDirectory = NormalizeProfileDirectory(profileDirectory);
        BaseDirectory = ResolveBaseDirectory(baseDirectory);
        ProfileHash = ComputeProfileHash(ProfileDirectory);
        _logger = logger;
    }

    /// <summary>规范化后的绝对 profile 目录。</summary>
    public string ProfileDirectory { get; }

    /// <summary>发现文件与客户端锁的根目录。</summary>
    public string BaseDirectory { get; }

    /// <summary>profile 目录的短 hash（发现文件名与客户端目录名都用它）。</summary>
    public string ProfileHash { get; }

    /// <summary>本 profile 的发现文件完整路径。</summary>
    public string InfoPath => ComputeInfoPath(ProfileDirectory, BaseDirectory);

    /// <summary>本 profile 的客户端存活锁 / 页面清单目录。</summary>
    public string ClientsDirectory => Path.Combine(BaseDirectory, "clients", ProfileHash);

    /// <summary>
    /// 解析宿主的**固定** profile 目录（与 <see cref="BrowserOptions.PersistLogin"/> 无关）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须有这个纯函数</b>：宿主单例键与发现文件 hash 都来自 profile 路径。
    /// 若两边各自现算（宿主用持久目录、客户端在 <c>PersistLogin=false</c> 时用临时目录），
    /// 同一个浏览器会有两个身份 —— 客户端永远找不到正在跑的宿主，每次都再拉起一个。
    /// </para>
    /// <para>
    /// <b>为什么与 <c>PersistLogin</c> 无关</b>：<c>PersistLogin</c> 只决定**页面上下文**
    /// （复用 DefaultContext 的 cookie，还是开隔离 BrowserContext），不决定 Chromium 的
    /// <c>user-data-dir</c>。宿主始终要有固定目录，否则它没有稳定身份。
    /// </para>
    /// </remarks>
    /// <param name="configured">配置里的 profile 目录；空白视为未配置。</param>
    /// <param name="userHome">用户主目录（测试注入用）；空白 = 当前用户主目录。</param>
    public static string ResolveHostProfileDirectory(string? configured, string? userHome = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome;

        return Path.Combine(home, ".seeing", ProfileDirectoryName);
    }

    /// <summary>默认根目录：<c>~/.seeing/browser</c>。</summary>
    /// <param name="baseDirectory">显式根目录；空白视为默认。</param>
    public static string ResolveBaseDirectory(string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
            return Path.GetFullPath(baseDirectory);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(home, ".seeing", RootDirectoryName);
    }

    /// <summary>把 profile 目录规范化成绝对路径并去掉尾部分隔符。</summary>
    /// <remarks>
    /// 去尾分隔符是身份稳定的必要条件：<c>C:\p\A</c> 与 <c>C:\p\A\</c> 是同一个目录，
    /// 若 hash 不同，宿主与客户端会各自维护一份「身份」。
    /// </remarks>
    public static string NormalizeProfileDirectory(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
    }

    /// <summary>计算 profile 目录的短 hash（8 位十六进制）。</summary>
    /// <remarks>
    /// <b>为什么不区分大小写</b>：Windows / macOS 默认文件系统对路径大小写不敏感，
    /// 同一目录的大小写不同写法必须映射到同一身份，否则会算出两个发现文件。
    /// Linux 保留大小写（两条路径确实可能是两个目录）。
    /// </remarks>
    public static string ComputeProfileHash(string profileDirectory)
    {
        var normalized = NormalizeProfileDirectory(profileDirectory);
        var key = OperatingSystem.IsLinux() ? normalized : normalized.ToLowerInvariant();

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(digest.AsSpan(0, 4)).ToLowerInvariant();
    }

    /// <summary>计算某 profile 的发现文件路径。</summary>
    /// <param name="profileDirectory">profile 目录（绝对 / 相对均可）。</param>
    /// <param name="baseDirectory">发现文件根目录；空白 = <c>~/.seeing/browser</c>。</param>
    public static string ComputeInfoPath(string profileDirectory, string? baseDirectory = null)
        => Path.Combine(
            ResolveBaseDirectory(baseDirectory),
            InfoFilePrefix + ComputeProfileHash(profileDirectory) + InfoFileSuffix);

    /// <summary>读发现文件；不存在或内容不可解析时返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// <para>
    /// <b>为什么解析失败不抛</b>：发现文件可能被手工改坏、也可能是上一个版本留下的。
    /// 抛异常会让「启动浏览器」这条路径整体失败，而正确行为是「当作没有宿主，重新拉起」。
    /// </para>
    /// <para>
    /// <b>但必须记日志</b>：这条恢复路径本身没有任何用户可见症状（只是多拉起一个宿主），
    /// 不记日志就完全查不到「为什么总是重新拉起」。
    /// </para>
    /// </remarks>
    public BrowserHostInfo? Read()
    {
        try
        {
            if (!File.Exists(InfoPath))
                return null;

            using var stream = File.OpenRead(InfoPath);
            return JsonSerializer.Deserialize<BrowserHostInfo>(stream, s_readOptions);
        }
        catch (JsonException ex)
        {
            // 半截文件 / 手工改坏 / 旧版本字段类型变化：当作没有发现文件
            _logger?.LogDebug(ex, "发现文件内容无法解析，按「没有宿主」处理：{Path}", InfoPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "发现文件不可读，按「没有宿主」处理：{Path}", InfoPath);
            return null;
        }
    }

    /// <summary>原子写发现文件（唯一临时名 + 移动重试）。</summary>
    public void Write(BrowserHostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var json = JsonSerializer.Serialize(info, s_writeOptions);
        AtomicFileWrite.WriteAllTextAtomic(
            InfoPath, json, subject: "浏览器宿主发现文件", logger: _logger);

        _logger?.LogDebug(
            "已写发现文件：{Path}（generation={Generation}, hostPid={HostPid}, endpoint={Endpoint}）",
            InfoPath, info.Generation, info.HostPid, info.Endpoint);
    }

    /// <summary>
    /// 删除发现文件，**仅当它属于当前进程**；返回是否删除。
    /// </summary>
    /// <remarks>
    /// 不校验 PID 就删，会在「旧宿主退出晚于新宿主启动」时删掉新宿主的文件，
    /// 所有客户端随后都会因为读不到文件而重复拉起宿主。
    /// </remarks>
    public bool TryDelete()
    {
        var info = Read();
        if (info is null)
        {
            _logger?.LogDebug("没有可删除的发现文件（不存在或不可读）：{Path}", InfoPath);
            return false;
        }

        if (info.HostPid != Environment.ProcessId)
        {
            // 这不是错误：旧宿主退出时文件已被后继宿主接管，绝不能再删
            _logger?.LogDebug(
                "发现文件属于其他宿主（hostPid={OwnerPid}，本进程={CurrentPid}），保留：{Path}",
                info.HostPid, Environment.ProcessId, InfoPath);
            return false;
        }

        try
        {
            File.Delete(InfoPath);
            _logger?.LogDebug("已删除发现文件：{Path}", InfoPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "删除发现文件失败：{Path}", InfoPath);
            return false;
        }
    }

    /// <summary>发现文件协议版本是否与当前实现一致。</summary>
    /// <remarks>不匹配意味着「代码已升级但旧宿主还在跑」，调用方应提示重启浏览器而不是连接旧端点。</remarks>
    public bool VersionMatches(BrowserHostInfo? info)
    {
        if (info is not null && info.Version != CurrentVersion)
        {
            _logger?.LogDebug(
                "发现文件协议版本不匹配（文件={FileVersion}，当前={CurrentVersion}）：{Path}",
                info.Version, CurrentVersion, InfoPath);
        }

        return info is not null && info.Version == CurrentVersion;
    }

    /// <summary>
    /// 发现文件描述的是不是「指定 PID + 启动时刻」的那个宿主。
    /// </summary>
    /// <remarks>
    /// <para>
    /// PID 会被系统复用，单独比对 PID 会把无关进程当成宿主；因此启动时刻必须一起比。
    /// </para>
    /// <para>
    /// <b><paramref name="hostStartedAt"/> 必须只在宿主启动时算一次并持久化复用</b>：
    /// 不要用 <c>Process.StartTime</c> 现算后比较 —— 它受时区/精度/权限影响，
    /// 与发现文件里序列化的那个值不一定逐 tick 相等，结果是「明明是同一个宿主却判成不同」，
    /// 客户端于是反复重新拉起。
    /// </para>
    /// </remarks>
    /// <param name="info">发现文件内容。</param>
    /// <param name="hostPid">期望的宿主 PID。</param>
    /// <param name="hostStartedAt">期望的宿主启动时刻（宿主启动时算一次并沿用的值）。</param>
    public static bool IsSameHost(BrowserHostInfo? info, int hostPid, DateTimeOffset hostStartedAt)
        => info is not null
           && info.HostPid == hostPid
           && info.HostStartedAt == hostStartedAt;
}
