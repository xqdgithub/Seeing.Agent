using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.BuiltIn.Web
{
    /// <summary>
    /// 网页抓取工具 - 从指定 URL 获取内容
    /// </summary>
    public class WebFetchTool : ToolBase
    {
        private const int MaxResponseSize = 5 * 1024 * 1024; // 5MB
        private const int DefaultTimeoutSeconds = 30;
        private const int MaxTimeoutSeconds = 120;

        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, byte> _approvedHosts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 创建 WebFetchTool 实例
        /// </summary>
        public WebFetchTool(ILogger<WebFetchTool> logger, HttpClient httpClient) : base(logger)
        {
            _httpClient = httpClient;
        }

        /// <inheritdoc/>
        public override string Id => "webfetch";

        /// <inheritdoc/>
        public override string Description =>
            "从指定 URL 获取内容，支持返回文本、Markdown 或 HTML 格式。" +
            "可用于获取网页内容、API 响应或在线文档。" +
            "自动将 HTML 转换为 Markdown 或纯文本格式。";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                url = new
                {
                    type = "string",
                    description = "要获取内容的 URL"
                },
                format = new
                {
                    type = "string",
                    @enum = new[] { "text", "markdown", "html" },
                    @default = "markdown",
                    description = "返回内容的格式（text、markdown 或 html），默认为 markdown"
                },
                timeout = new
                {
                    type = "number",
                    description = "超时时间（秒，最大 120）"
                }
            },
            required = new[] { "url" }
        });

        /// <inheritdoc/>
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var url = GetStringArgument(arguments, "url");
            if (url == null)
            {
                return Failure("url 参数是必需的");
            }

            // 验证 URL
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("URL 必须以 http:// 或 https:// 开头");
            }

            // SSRF 防护：检查内网地址（同会话缓存已批准的域名）
            var approved = _approvedHosts;
            var safetyCheck = await ValidateUrlSafetyAsync(url, null, host => approved.TryAdd(host, 0));
            if (safetyCheck != null)
            {
                _logger.LogWarning("SSRF 防护拒绝请求: {Reason}, URL: {Url}", safetyCheck, url);
                return Failure($"请求被安全策略拒绝: {safetyCheck}");
            }

            var format = GetStringArgument(arguments, "format") ?? "markdown";
            var timeoutSeconds = GetIntArgument(arguments, "timeout") ?? DefaultTimeoutSeconds;
            timeoutSeconds = Math.Min(timeoutSeconds, MaxTimeoutSeconds);

            try
            {
                using var cts = new CancellationTokenSource(timeoutSeconds * 1000);
                var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cts.Token, context.CancellationToken).Token;

                // 构建请求
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                request.Headers.Add("Accept", BuildAcceptHeader(format));
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

                var response = await _httpClient.SendAsync(request, combinedToken);

                // 处理 Cloudflare 拦截
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                    response.Headers.Contains("cf-mitigated"))
                {
                    request.Headers.Remove("User-Agent");
                    request.Headers.Add("User-Agent", "Seeing.Agent");
                    response = await _httpClient.SendAsync(request, combinedToken);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Failure($"请求失败，状态码: {response.StatusCode}");
                }

                // 检查内容长度
                var contentLength = response.Content.Headers.ContentLength ?? 0;
                if (contentLength > MaxResponseSize)
                {
                    return Failure("响应过大（超过 5MB 限制）");
                }

                var contentBytes = await response.Content.ReadAsByteArrayAsync(combinedToken);
                if (contentBytes.Length > MaxResponseSize)
                {
                    return Failure("响应过大（超过 5MB 限制）");
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                // 检查是否为图片
                if (IsImageContentType(contentType))
                {
                    var base64Content = Convert.ToBase64String(contentBytes);
                    return Success("图片已获取", new Dictionary<string, object>
                    {
                        ["contentType"] = contentType,
                        ["base64"] = base64Content,
                        ["url"] = url
                    });
                }

                var content = Encoding.UTF8.GetString(contentBytes);

                // 根据格式处理内容
                var output = ProcessContent(content, contentType, format);

                return Success(output, new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["contentType"] = contentType,
                    ["format"] = format,
                    ["size"] = contentBytes.Length
                });
            }
            catch (OperationCanceledException)
            {
                return Failure("请求超时");
            }
            catch (Exception ex)
            {
                return Failure(ex, "获取网页内容失败");
            }
        }

        /// <summary>
        /// 构建 Accept Header
        /// </summary>
        private static string BuildAcceptHeader(string format)
        {
            return format switch
            {
                "markdown" => "text/markdown;q=1.0, text/x-markdown;q=0.9, text/plain;q=0.8, text/html;q=0.7, */*;q=0.1",
                "text" => "text/plain;q=1.0, text/markdown;q=0.9, text/html;q=0.8, */*;q=0.1",
                "html" => "text/html;q=1.0, application/xhtml+xml;q=0.9, text/plain;q=0.8, */*;q=0.1",
                _ => "*/*"
            };
        }

        /// <summary>
        /// 检查是否为图片类型
        /// </summary>
        private static bool IsImageContentType(string contentType)
        {
            return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                   contentType != "image/svg+xml";
        }

        /// <summary>
        /// 处理内容
        /// </summary>
        private static string ProcessContent(string content, string contentType, string format)
        {
            var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

            if (!isHtml)
            {
                return content;
            }

            return format switch
            {
                "markdown" => ConvertHtmlToMarkdown(content),
                "text" => ExtractTextFromHtml(content),
                "html" => content,
                _ => content
            };
        }

        /// <summary>
        /// 从 HTML 提取纯文本（简单实现）
        /// </summary>
        private static string ExtractTextFromHtml(string html)
        {
            // 移除 script、style、noscript 等标签内容
            var patterns = new[]
            {
                @"<script[^>]*>.*?</script>",
                @"<style[^>]*>.*?</style>",
                @"<noscript[^>]*>.*?</noscript>",
                @"<!--.*?-->",
                @"<head[^>]*>.*?</head>"
            };

            foreach (var pattern in patterns)
            {
                html = Regex.Replace(html, pattern, "",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            // 移除所有 HTML 标签
            html = Regex.Replace(html, @"<[^>]+>", "");

            // 解码 HTML 实体
            html = System.Net.WebUtility.HtmlDecode(html);

            // 清理空白
            html = Regex.Replace(html, @"\s+", " ");

            return html.Trim();
        }

        /// <summary>
        /// 将 HTML 转换为 Markdown（简单实现）
        /// </summary>
        private static string ConvertHtmlToMarkdown(string html)
        {
            // 移除 script、style、meta、link 标签
            var removePatterns = new[]
            {
                @"<script[^>]*>.*?</script>",
                @"<style[^>]*>.*?</style>",
                @"<meta[^>]*>",
                @"<link[^>]*>"
            };

            foreach (var pattern in removePatterns)
            {
                html = Regex.Replace(html, pattern, "",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            // 转换标题
            for (int i = 1; i <= 6; i++)
            {
                html = Regex.Replace(html, $@"<h{i}[^>]*>(.*?)</h{i}>",
                    $"{new string('#', i)} $1",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            // 转换链接 - 使用字符类匹配引号
            html = Regex.Replace(html, @"<a[^>]*href=[""']([^""']+)[""'][^>]*>(.*?)</a>",
                "[$2]($1)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 转换图片
            html = Regex.Replace(html, @"<img[^>]*src=[""']([^""']+)[""'][^>]*alt=[""']([^""']+)[""'][^>]*>",
                "![$2]($1)",
                RegexOptions.IgnoreCase);

            // 转换粗体
            html = Regex.Replace(html, @"<(b|strong)[^>]*>(.*?)</(b|strong)>",
                "**$2**",
                RegexOptions.IgnoreCase);

            // 转换斜体
            html = Regex.Replace(html, @"<(i|em)[^>]*>(.*?)</(i|em)>",
                "*$2*",
                RegexOptions.IgnoreCase);

            // 转换代码块
            html = Regex.Replace(html, @"<pre[^>]*><code[^>]*>(.*?)</code></pre>",
                "```\n$1\n```",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 转换内联代码
            html = Regex.Replace(html, @"<code[^>]*>(.*?)</code>",
                "`$1`",
                RegexOptions.IgnoreCase);

            // 转换列表项
            html = Regex.Replace(html, @"<li[^>]*>(.*?)</li>",
                "- $1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 转换段落
            html = Regex.Replace(html, @"<p[^>]*>(.*?)</p>",
                "$1\n\n",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 转换换行
            html = Regex.Replace(html, @"<br\s*/?>", "\n",
                RegexOptions.IgnoreCase);

            // 移除剩余标签
            html = Regex.Replace(html, @"<[^>]+>", "");

            // 解码 HTML 实体
            html = System.Net.WebUtility.HtmlDecode(html);

            // 清理多余空白
            html = Regex.Replace(html, @"\n{3,}", "\n\n");
            html = Regex.Replace(html, @"^\s+", "", RegexOptions.Multiline);

            return html.Trim();
        }

        private static readonly IPAddress[] s_blockedIpv4Prefixes =
        [
            IPAddress.Parse("10.0.0.0"),
            IPAddress.Parse("172.16.0.0"),
            IPAddress.Parse("192.168.0.0"),
            IPAddress.Parse("127.0.0.0"),
            IPAddress.Parse("169.254.0.0"),
            IPAddress.Parse("0.0.0.0"),
        ];

        private static readonly IPAddress[] s_blockedIpv4Masks =
        [
            IPAddress.Parse("255.0.0.0"),
            IPAddress.Parse("255.240.0.0"),
            IPAddress.Parse("255.255.0.0"),
            IPAddress.Parse("255.0.0.0"),
            IPAddress.Parse("255.255.0.0"),
            IPAddress.Parse("255.0.0.0"),
        ];

        private static bool IsPrivateOrReserved(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                for (int i = 0; i < s_blockedIpv4Prefixes.Length; i++)
                {
                    var prefix = s_blockedIpv4Prefixes[i].GetAddressBytes();
                    var mask = s_blockedIpv4Masks[i].GetAddressBytes();
                    bool match = true;
                    for (int j = 0; j < 4; j++)
                    {
                        if ((bytes[j] & mask[j]) != (prefix[j] & mask[j]))
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) return true;
                }
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (IPAddress.IPv6Loopback.Equals(address)) return true;
                if (address.IsIPv6LinkLocal) return true;
                var b = address.GetAddressBytes();
                if (b[0] == 0xfc || b[0] == 0xfd) return true;
                if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
            }

            return false;
        }

        private static async Task<string?> ValidateUrlSafetyAsync(string url, HashSet<string>? cachedApproved, Action<string> addToCache)
        {
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                if (cachedApproved != null && cachedApproved.Contains(host))
                    return null;

                if (host is "localhost" or "127.0.0.1" or "[::1]" ||
                    host.EndsWith(".local") || host.EndsWith(".internal"))
                    return "禁止访问内网地址";

                var addresses = await Dns.GetHostAddressesAsync(uri.Host);
                foreach (var addr in addresses)
                {
                    if (IsPrivateOrReserved(addr))
                        return $"禁止访问私有/内网 IP: {addr} ({uri.Host})";
                }

                addToCache(host);
                return null;
            }
            catch (Exception ex)
            {
                return $"URL 安全验证失败: {ex.Message}";
            }
        }
    }
}
