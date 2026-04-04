using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.Web
{
    /// <summary>
    /// 代码搜索工具 - 使用 Exa MCP API 搜索代码和文档
    /// </summary>
    public class CodeSearchTool : ToolBase
    {
        private const string ApiBaseUrl = "https://mcp.exa.ai";
        private const string ApiEndpoint = "/mcp";
        private const int DefaultTimeoutMs = 30000;
        private const int DefaultTokensNum = 5000;
        private const int MinTokensNum = 1000;
        private const int MaxTokensNum = 50000;

        private readonly HttpClient _httpClient;

        /// <summary>
        /// 创建 CodeSearchTool 实例
        /// </summary>
        public CodeSearchTool(ILogger<CodeSearchTool> logger, HttpClient httpClient) : base(logger)
        {
            _httpClient = httpClient;
        }

        /// <inheritdoc/>
        public override string Id => "codesearch";

        /// <inheritdoc/>
        public override string Description =>
            "使用 Exa 搜索引擎搜索代码示例、API 文档和 SDK 使用方法。" +
            "适合查找特定库或框架的代码示例、最佳实践和文档。" +
            "例如：'React useState hook examples'、'Python pandas dataframe filtering'等。";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "搜索查询字符串，用于查找 API、库和 SDK 的相关代码示例。例如：'React useState hook examples'、'Python pandas dataframe filtering'、'Express.js middleware'"
                },
                tokensNum = new
                {
                    type = "number",
                    minimum = MinTokensNum,
                    maximum = MaxTokensNum,
                    @default = DefaultTokensNum,
                    description = "返回的 Token 数量（1000-50000）。默认 5000。根据需要的上下文量调整 - 使用较小的值进行精确查询，使用较大的值获取完整文档。"
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

            var tokensNum = GetIntArgument(arguments, "tokensNum") ?? DefaultTokensNum;
            tokensNum = Math.Clamp(tokensNum, MinTokensNum, MaxTokensNum);

            // 请求权限确认
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "codesearch",
                    Patterns = new List<string> { query },
                    Metadata = new Dictionary<string, object>
                    {
                        ["query"] = query,
                        ["tokensNum"] = tokensNum
                    }
                });
            }

            try
            {
                // 构建 MCP 请求
                var mcpRequest = new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    parameters = new
                    {
                        name = "get_code_context_exa",
                        arguments = new Dictionary<string, object?>
                        {
                            ["query"] = query,
                            ["tokensNum"] = tokensNum
                        }
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
                    return Failure($"代码搜索错误 ({response.StatusCode}): {errorText}");
                }

                var responseText = await response.Content.ReadAsStringAsync(combinedToken);

                // 解析 SSE 响应
                var output = ParseSseResponse(responseText);
                if (output == null)
                {
                    output = "未找到代码示例或文档。请尝试不同的查询，更具体地描述库或编程概念，或检查框架名称拼写。";
                }

                return Success($"代码搜索: {query}", output, new Dictionary<string, object>
                {
                    ["query"] = query,
                    ["tokensNum"] = tokensNum
                });
            }
            catch (OperationCanceledException)
            {
                return Failure("代码搜索请求超时");
            }
            catch (Exception ex)
            {
                return Failure(ex, "代码搜索失败");
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