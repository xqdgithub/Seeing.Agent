namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 浏览器配置（配置节 <c>Browser</c>，来自 <c>~/.seeing/seeing.json</c> 与 <c>./.seeing/seeing.json</c>）。
/// </summary>
/// <remarks>
/// <b>为什么这些项可配</b>：浏览器可执行文件位置、是否开窗、视口大小在不同机器与不同用途下必须不同
/// （CI 无 GUI、笔记本小屏、需要人工登录时要有头）。写死任一项都会让某类宿主直接不可用。
/// </remarks>
public sealed class BrowserOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Browser";

    /// <summary>
    /// 是否无头运行。默认 <see langword="true"/>。
    /// <para>需要人工登录或观察时设为 <see langword="false"/>（有头窗口，但页面仍由 agent 经 CDP 驱动）。</para>
    /// </summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// 浏览器可执行文件显式路径。为 <see langword="null"/> 时按「Edge → Chrome → 已下载 Chromium」探测。
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// 页面静默多少秒后回收（只关页面与 context，不关浏览器进程）。0 表示不回收。
    /// </summary>
    public int IdleSeconds { get; set; } = 600;

    /// <summary>视口宽（像素）。screencast 与快照共用。</summary>
    public int ViewportWidth { get; set; } = 1280;

    /// <summary>视口高（像素）。</summary>
    public int ViewportHeight { get; set; } = 800;

    /// <summary>
    /// 画面流出图倍率。默认 <c>0</c>：跟随前端 <c>devicePixelRatio</c>（文字最清晰）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它只影响编码像素数，不影响网页排版</b>：视口始终是面板的 CSS 尺寸
    /// （<c>deviceScaleFactor</c> = dpr），帧的像素数 = 面板尺寸 × 本倍率。
    /// 默认倍率 = dpr，因此帧与画布 backing 1:1，流本身就是清晰的物理像素 ——
    /// 不需要「补一张静帧」的第二来源（那个双源设计曾导致画面反复闪）。
    /// </para>
    /// <para>
    /// 设为 <c>1</c> 可退回 CSS 像素出图：带宽与编码成本最省，但 HiDPI 屏上文字会偏软
    /// （画布被浏览器放大显示）。1–2 之间的值按固定倍率出图。
    /// 这是运行期项：下一次建页/尺寸变更生效，不需要重启浏览器进程。
    /// </para>
    /// </remarks>
    public double StreamPixelRatio { get; set; }


    /// <summary>
    /// 是否允许导航到内网/回环地址。默认 <see langword="false"/>（与 <c>webfetch</c> 的 SSRF 策略同源）。
    /// </summary>
    public bool AllowPrivateHosts { get; set; }

    /// <summary>
    /// 是否复用持久 profile 以保留登录态。默认 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么默认开</b>：浏览器自动化最常见的真实需求是「在我已登录的站点上替我办事」。
    /// 临时 profile 每次都是全新访客，登录态、语言偏好、验证码豁免全部丢失，
    /// 用户必须每轮重新登录，工具价值接近于零。
    /// </para>
    /// <para>
    /// <b>为什么这条开关会改变上下文语义</b>：Chromium 只有<b>默认上下文</b>会把 cookie 写盘，
    /// <c>CreateBrowserContextAsync</c> 创建的隔离上下文等同于无痕窗口 —— 实测重启后 cookie 为 0。
    /// 因此开启时所有会话共用 <c>DefaultContext</c>，<b>cookie 跨会话共享</b>；
    /// 关闭时恢复「每会话一个隔离上下文」。
    /// </para>
    /// <para>
    /// <b>为什么不能复用用户自己的 Edge 登录态</b>（实测结论）：
    /// ① Edge 运行时对 <c>Network\Cookies</c> 加独占锁，任何共享模式都打不开；
    /// ② Chromium 136+ 禁止对默认 profile 开启远程调试（端口不可达）；
    /// ③ <c>Local State</c> 含 <c>app_bound_encrypted_key</c>，cookie 密钥绑定 app 身份，换进程解不开。
    /// 所以只能「在专用 profile 里登录一次，之后永久复用」。
    /// </para>
    /// </remarks>
    public bool PersistLogin { get; set; } = true;

    /// <summary>
    /// 持久 profile 目录（同时也是宿主进程的固定身份与 Chromium 的 <c>user-data-dir</c>）。
    /// 为 <see langword="null"/> 时用 <c>{用户目录}/.seeing/browser-profile</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它决定宿主身份，与 <see cref="PersistLogin"/> 无关</b>：发现文件的 hash、宿主单例锁、
    /// 客户端存活锁目录全部由它派生。宿主必须始终有一个稳定目录，否则同一台机器上的客户端
    /// 会各自认出「另一个宿主」并重复拉起 Chromium。
    /// </para>
    /// <para>
    /// <see cref="PersistLogin"/> 只决定<b>页面上下文</b>（复用 DefaultContext 的 cookie，
    /// 还是每会话一个隔离 <c>BrowserContext</c>），不决定 <c>user-data-dir</c>。
    /// </para>
    /// <para>
    /// 允许覆盖是为了让测试与多实例部署能各自隔离 profile ——
    /// 同一 profile 目录同时被两个浏览器进程打开会被 Chromium 直接拒绝启动
    /// （<c>The browser is already running for ...</c>）。改动需重启浏览器才生效。
    /// </para>
    /// </remarks>
    public string? ProfileDirectory { get; set; }

    /// <summary>
    /// 宿主空闲退出秒数：连续这么久没有任何存活客户端时，宿主进程自行退出。0 = 不自动退出。
    /// </summary>
    /// <remarks>
    /// 客户端把它作为 <c>--idle-seconds</c> 传给宿主。它只在宿主启动时读取，
    /// 因此改动需要重启浏览器（进进程级签名）。
    /// </remarks>
    public int HostIdleSeconds { get; set; } = 1800;

    /// <summary>单次浏览器操作超时上限（秒）。</summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 是否启用反自动化检测（移除自动化标志、保留 GPU、伪装 UA）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这个开关</b>：默认启动参数里有 <c>--no-sandbox</c>、<c>--disable-gpu</c>、
    /// <c>navigator.webdriver = true</c> 等几处自动化特征。这些组合起来足以让百度、Google、
    /// 各类验证码服务判定「这是机器人」。用户实测：同一台机器上，本机 Edge 访问百度正常，
    /// 而工作台里的浏览器会触发真人验证。
    /// </para>
    /// <para>
    /// <b>开启后做什么</b>：
    /// ① <c>--disable-blink-features=AutomationControlled</c> 消除 <c>navigator.webdriver</c>；
    /// ② 不再传 <c>--no-sandbox</c> / <c>--disable-gpu</c>（WebGL 软件渲染是强指纹）；
    /// ③ 注入脚本伪造 <c>navigator.plugins</c> / <c>languages</c> / WebGL vendor；
    /// ④ 去掉 UA 里的 <c>HeadlessChrome</c> 标记。
    /// </para>
    /// <para>
    /// <b>代价</b>：需要真实的 GPU 与可用的沙箱。极少数受限环境（精简容器）可能起不来，
    /// 那时设为 <see langword="false"/>，或在 <see cref="ExtraArgs"/> 里自行补回参数。
    /// </para>
    /// <para>
    /// <b>这不是银弹</b>：反爬是持续对抗，IP 信誉、账号历史、行为轨迹同样重要。
    /// 最有效的一招仍是 <see cref="Headless"/> 设为 <see langword="false"/>。
    /// </para>
    /// </remarks>
    public bool AntiDetection { get; set; } = true;

    /// <summary>额外的浏览器启动参数（追加在默认参数之后，可用于覆盖）。</summary>
    public string[]? ExtraArgs { get; set; }

    /// <summary>自定义 User-Agent；为 <see langword="null"/> 时按 <see cref="AntiDetection"/> 自动处理。</summary>
    /// <remarks>
    /// 无头模式默认 UA 含 <c>HeadlessChrome</c> 字样——这是最容易被识别的标记之一。
    /// 留空时：反检测开启则去掉该标记，关闭则保留浏览器原生 UA。
    /// </remarks>
    public string? UserAgent { get; set; }

    /// <summary>取视口宽（下限保护：配置写坏不让页面变成 1px）。</summary>
    /// <remarks>
    /// <b>只读计算属性必须标 <see cref="JsonIgnoreAttribute"/></b>：设置页保存时会把整个
    /// <see cref="BrowserOptions"/> 序列化写盘，不标注就会把 <c>EffectiveWidth</c>、
    /// <c>IdleTimeout</c> 这类派生值一起写进 seeing.json ——
    /// 它们既无法被读取回来（没有 setter），又让配置文件充满噪声，
    /// 用户看到会以为是可以手改的配置项。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveWidth => Math.Clamp(ViewportWidth, 320, 4096);

    /// <summary>取视口高。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveHeight => Math.Clamp(ViewportHeight, 240, 4096);

    /// <summary>取操作超时（下限 1 秒、上限 300 秒）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan OperationTimeout
        => TimeSpan.FromSeconds(Math.Clamp(DefaultTimeoutSeconds, 1, 300));

    /// <summary>取空闲回收时长；≤0 视为禁用回收。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? IdleTimeout
        => IdleSeconds > 0 ? TimeSpan.FromSeconds(IdleSeconds) : null;

    /// <summary>
    /// 取实际出图倍率：<c>0</c> = 跟随设备像素比（返回 <c>0</c>，由调用方代入 dpr）。
    /// </summary>
    /// <remarks>
    /// 夹在 1–2：&lt;1 会让文字比 CSS 像素还糊，&gt;2 只徒增编码成本（前端 dpr 同样按 2 封顶）。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public double EffectiveStreamRatio
        => double.IsFinite(StreamPixelRatio) && StreamPixelRatio > 0
            ? Math.Clamp(StreamPixelRatio, 1d, 2d)
            : 0d;
}
