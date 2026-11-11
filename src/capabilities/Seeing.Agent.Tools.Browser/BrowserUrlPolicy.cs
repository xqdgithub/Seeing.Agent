using System.Net;
using System.Net.Sockets;

namespace Seeing.Agent.Tools.Browser;

/// <summary>URL 准入判定结果。</summary>
/// <param name="Allowed">是否放行。</param>
/// <param name="Reason">拒绝原因（放行时为 <see langword="null"/>）。</param>
public readonly record struct BrowserUrlVerdict(bool Allowed, string? Reason)
{
    /// <summary>放行。</summary>
    public static BrowserUrlVerdict Allow() => new(true, null);

    /// <summary>拒绝并给出可读原因。</summary>
    public static BrowserUrlVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// 导航地址准入策略：唯一判定点，工具与工作台地址栏共用。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有</b>：agent 能导航意味着能访问<b>宿主进程可达的任何地址</b>，包括云元数据服务
/// （<c>169.254.169.254</c>）、内网管理后台、本机回环上的其它服务。这与 <c>webfetch</c> 面临的问题同源，
/// 策略也保持一致：默认拒绝私网/回环/链路本地。
/// </para>
/// <para>
/// <b>为什么按字面 IP 与 localhost 名判定，而不做 DNS 解析</b>：DNS 解析后再校验存在 TOCTOU
/// （校验后域名重新解析到内网）。浏览器导航无法像 HttpClient 那样固定连接目标，因此这一层的
/// 边界是「明显的内网字面量」；真正的强隔离应由宿主网络策略承担。这一点写在这里，避免后来者误以为它是完整 SSRF 防护。
/// </para>
/// </remarks>
public static class BrowserUrlPolicy
{
    /// <summary><c>data:text/html</c> 页面允许的最大字符长度（调试用的小页面）。</summary>
    public const int MaxDataUrlLength = 256 * 1024;

    /// <summary>判定地址是否可用于导航。</summary>
    /// <param name="url">目标地址。</param>
    /// <param name="allowPrivateHosts">是否放行私网/回环（来自配置）。</param>
    public static BrowserUrlVerdict Evaluate(string? url, bool allowPrivateHosts)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BrowserUrlVerdict.Deny("地址为空");

        var trimmed = url.Trim();

        // about:blank：清空页面的唯一合法方式，不需要网络
        if (string.Equals(trimmed, "about:blank", StringComparison.OrdinalIgnoreCase))
            return BrowserUrlVerdict.Allow();

        // data:text/html：无网络调试页面；其它 data 类型（图片/PDF/任意 MIME）不开放
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!trimmed.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
                return BrowserUrlVerdict.Deny("只允许 data:text/html 形式的本地页面");

            if (trimmed.Length > MaxDataUrlLength)
                return BrowserUrlVerdict.Deny($"data:text/html 页面过大（超过 {MaxDataUrlLength} 字符）");

            return BrowserUrlVerdict.Allow();
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return BrowserUrlVerdict.Deny("不是合法的绝对地址（需要 http:// 或 https:// 开头）");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return BrowserUrlVerdict.Deny(
                $"不支持的协议 '{uri.Scheme}'：只允许 http、https、about:blank、data:text/html");
        }

        if (allowPrivateHosts)
            return BrowserUrlVerdict.Allow();

        if (IsPrivateHost(uri.Host))
        {
            return BrowserUrlVerdict.Deny(
                $"目标地址属于内网或回环（{uri.Host}），已被拒绝；" +
                "如确需访问请在 seeing.json 设 Browser.AllowPrivateHosts=true");
        }

        return BrowserUrlVerdict.Allow();
    }

    /// <summary>
    /// 主机名是否指向私网/回环/链路本地（含 <c>localhost</c> 与私网字面 IP）。
    /// </summary>
    /// <param name="host">主机名或 IP 字面量。</param>
    public static bool IsPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        // TrimEnd('.') 是安全关键，不是洁癖：`Uri.Host` 会保留末尾点，
        // 而 DNS 与浏览器把 `localhost.` / `127.0.0.1.` 当作**绝对 FQDN** 正常解析到回环。
        // 不去尾点时 `localhost.` 既不等于 "localhost"、`IPAddress.TryParse("127.0.0.1.")` 也失败
        // → 两个字面量各绕过一次内网判定（实测可用）。
        var name = host.Trim().Trim('[', ']').TrimEnd('.');

        if (name.Length == 0)
            return true;    // 形如 "." 的畸形主机名：按不可用处理

        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "localhost.localdomain", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 内网常见后缀：这些域名在企业网里几乎总是指向内部服务
        // （打印机 `printer.local`、内部系统 `wiki.internal`、K8s 服务 `svc.cluster.local`）。
        // 与 WebFetchTool 的后缀规则对齐——但注意浏览器侧**不做 DNS 解析**：
        // 导航无法像 HttpClient 那样固定连接目标，解析后再校验存在 TOCTOU。
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cluster.local", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(name, out var address))
            return false;   // 普通域名：不做 DNS 解析（见类型注释的边界说明）

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                   // 0.0.0.0/8
                10 => true,                                  // 10/8
                127 => true,                                 // 127/8
                169 when b[1] == 254 => true,                // 169.254/16（含云元数据）
                172 when b[1] is >= 16 and <= 31 => true,     // 172.16/12
                192 when b[1] == 168 => true,                // 192.168/16
                100 when b[1] is >= 64 and <= 127 => true,    // 100.64/10（CGNAT）
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
                return true;

            var b = address.GetAddressBytes();
            // fc00::/7 唯一本地地址
            if ((b[0] & 0xFE) == 0xFC)
                return true;

            // IPv4-mapped ::ffff:a.b.c.d → 递归按 IPv4 判定
            if (address.IsIPv4MappedToIPv6)
                return IsPrivateHost(address.MapToIPv4().ToString());
        }

        return false;
    }
}
