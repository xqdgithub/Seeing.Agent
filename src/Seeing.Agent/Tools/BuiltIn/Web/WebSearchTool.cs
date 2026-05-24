using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.Web
{
    /// <summary>
    /// 网络搜索工具 - 使用 Exa MCP API 进行网络搜索
    /// </summary>
    public class WebSearchTool : ToolBase
    {
        private const string ApiBaseUrl = "https://mcp.exa.ai";
        private const string ApiEndpoint = "/mcp";
        private const int DefaultNumResults = 8;
        private const int DefaultTimeoutMs = 25000;

        private readonly HttpClient _httpClient;

        /// <summary>
        /// 创建 WebSearchTool 实例
        /// </summary>
        public WebSearchTool(ILogger<WebSearchTool> logger, HttpClient httpClient) : base(logger)
        {
            _httpClient = httpClient;
        }

        /// <inheritdoc/>
        public override string Id => "websearch";

        /// <inheritdoc/>
        public override string Description =>
            "使用 Exa 搜索引擎进行网络搜索，返回高质量的搜索结果。" +
            "支持实时抓取模式，可以获取最新的网页内容。" +
            "适合用于查找信息、新闻、文档等。";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "搜索查询字符串"
                },
                numResults = new
                {
                    type = "number",
                    description = "返回结果数量（默认: 8）"
                },
                livecrawl = new
                {
                    type = "string",
                    @enum = new[] { "fallback", "preferred" },
                    description = "实时抓取模式 - 'fallback': 缓存不可用时启用实时抓取，'preferred': 优先使用实时抓取（默认: 'fallback'）"
                },
                searchType = new
                {
                    type = "string",
                    @enum = new[] { "auto", "fast", "deep" },
                    description = "搜索类型 - 'auto': 平衡搜索（默认），'fast': 快速搜索，'deep': 深度搜索"
                },
                contextMaxCharacters = new
                {
                    type = "number",
                    description = "上下文最大字符数，优化用于 LLM（默认: 10000）"
                }
            },
            required = new[] { "query" }
        });

        /// <inheritdoc/>
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var query = GetStringArgument(arguments, "query");
            if (query == null)
            {
                return Failure("query 参数是必需的");
            }

            var numResults = GetIntArgument(arguments, "numResults") ?? DefaultNumResults;
            var livecrawl = GetStringArgument(arguments, "livecrawl") ?? "fallback";
            var searchType = GetStringArgument(arguments, "searchType") ?? "auto";
            var contextMaxCharacters = GetIntArgument(arguments, "contextMaxCharacters");

            // 请求权限确认
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "websearch",
                    Patterns = new List<string> { query },
                    Metadata = new Dictionary<string, object>
                    {
                        ["query"] = query,
                        ["numResults"] = numResults,
                        ["livecrawl"] = livecrawl,
                        ["type"] = searchType,
                        ["contextMaxCharacters"] = contextMaxCharacters
                    }
                });
            }

            try
            {
                // 构建 MCP 请求参数
                var mcpArguments = new Dictionary<string, object?>
                {
                    ["query"] = query,
                    ["type"] = searchType,
                    ["numResults"] = numResults,
                    ["livecrawl"] = livecrawl
                };

                if (contextMaxCharacters.HasValue)
                {
                    mcpArguments["contextMaxCharacters"] = contextMaxCharacters.Value;
                }

                var mcpRequest = new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new Dictionary<string, object>
                    {
                        ["name"] = "web_search_exa",
                        ["arguments"] = mcpArguments
                    }
                };

                using var cts = new CancellationTokenSource(DefaultTimeoutMs);
                var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cts.Token, context.CancellationToken).Token;

                var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}{ApiEndpoint}");
                request.Headers.Add("Accept", "application/json, text/event-stream");
                request.Content = new StringContent(
                    JsonSerializer.Serialize(mcpRequest),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.SendAsync(request, combinedToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(combinedToken);
                    return Failure($"搜索错误 ({response.StatusCode}): {errorText}");
                }

                var responseText = await response.Content.ReadAsStringAsync(combinedToken);

                // 解析 SSE 响应
                var output = ParseSseResponse(responseText);
                if (output == null)
                {
                    output = "未找到搜索结果，请尝试不同的查询。";
                }

                return Success($"网络搜索: {query}", output, new Dictionary<string, object>
                {
                    ["query"] = query,
                    ["numResults"] = numResults,
                    ["livecrawl"] = livecrawl,
                    ["type"] = searchType
                });
            }
            catch (OperationCanceledException)
            {
                return Failure("搜索请求超时");
            }
            catch (Exception ex)
            {
                return Failure(ex, "网络搜索失败");
            }
        }

        /// <summary>
        /// 解析 SSE 响应
        /// </summary>
        private static string? ParseSseResponse(string responseText)
        {
            var lines = responseText.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("data: "))
                {
                    try
                    {
                        var jsonText = line.Substring(6);
                        var data = JsonSerializer.Deserialize<JsonElement>(jsonText);

                        if (data.TryGetProperty("result", out var result) &&
                            result.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in content.EnumerateArray())
                            {
                                if (item.TryGetProperty("type", out var type) &&
                                    type.GetString() == "text" &&
                                    item.TryGetProperty("text", out var text))
                                {
                                    return text.GetString();
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // 忽略解析错误
                    }
                }
            }

            return null;
        }
    }
}
