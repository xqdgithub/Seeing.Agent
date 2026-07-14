using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// Anthropic 客户端 - 使用 HTTP API 发送请求
/// 只负责发送请求和接收响应，不负责模型定义
/// </summary>
public class AnthropicClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;

    /// <summary>Anthropic API 版本</summary>
    private const string ApiVersion = "2023-06-01";

    /// <summary>Provider ID</summary>
    public string ProviderId => _config.Id;

    /// <summary>Provider 类型</summary>
    public ProviderType ProviderType => ProviderType.Anthropic;

    /// <summary>
    /// 创建 Anthropic 客户端
    /// </summary>
    public AnthropicClient(ProviderConfig config, HttpClient httpClient, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient;

        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("ApiKey is required", nameof(config));

        // 配置 HTTP 客户端
        var baseUrl = config.BaseUrl ?? "https://api.anthropic.com";

        // 确保 Base URL 以 / 结尾，这样相对路径才能正确追加
        // 例如：https://seeingyou.top/llm/router/anthropic/ + v1/messages
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
            baseUrl += "/";

        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromMilliseconds(config.Timeout > 0 ? config.Timeout : 300000);

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);

        // 使用特定格式的 User-Agent（跳过验证以支持多 product token 格式）
        // 格式：opencode/1.2.26 ai-sdk/provider-utils/3.0.21 runtime/bun/1.3.10
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "opencode/1.2.26 ai-sdk/provider-utils/3.0.21 runtime/bun/1.3.10");

        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

        _logger.LogDebug("Anthropic 客户端已初始化: {ProviderId}, BaseUrl={BaseUrl}",
            ProviderId, baseUrl);
    }

    /// <summary>
    /// 发送聊天补全请求
    /// </summary>
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Anthropic 请求: Model={Model}, Messages={Count}", request.Model, request.Messages.Count);

        var anthropicRequest = BuildAnthropicRequest(request, false);
        var json = JsonSerializer.Serialize(anthropicRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("messages", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var anthropicResponse = JsonSerializer.Deserialize<AnthropicResponse>(responseBody);

        return MapResponse(anthropicResponse!, request.Model);
    }

    /// <summary>
    /// 发送流式聊天补全请求
    /// </summary>
    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Anthropic 流式请求: Model={Model}", request.Model);

        var anthropicRequest = BuildAnthropicRequest(request, true);
        var json = JsonSerializer.Serialize(anthropicRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var responseId = string.Empty;
        var contentBuilder = new StringBuilder();
        var toolBlocks = new Dictionary<int, AnthropicStreamingToolBlock>();
        var streamFinalizeSent = false;
        TokenUsage? lastStreamUsage = null;
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var eventData = line[5..];
            if (eventData == "[DONE]")
                continue;

            AnthropicStreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<AnthropicStreamEvent>(eventData, jsonOpts);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Anthropic 流事件 JSON 解析失败，已跳过片段");
                continue;
            }

            if (evt == null)
                continue;

            if (string.Equals(evt.Type, "error", StringComparison.Ordinal))
            {
                var errMsg = evt.Error?.Message ?? "Anthropic 流式错误";
                throw new InvalidOperationException(errMsg);
            }

            responseId = evt.Message?.Id ?? "";

            if (string.Equals(evt.Type, "content_block_start", StringComparison.Ordinal)
                && evt.Index is >= 0
                && evt.ContentBlock != null
                && string.Equals(evt.ContentBlock.Type, "tool_use", StringComparison.Ordinal))
            {
                toolBlocks[evt.Index.Value] = new AnthropicStreamingToolBlock
                {
                    Id = evt.ContentBlock.Id ?? "",
                    Name = evt.ContentBlock.Name ?? ""
                };
            }

            if (string.Equals(evt.Type, "content_block_delta", StringComparison.Ordinal)
                && evt.Index is >= 0
                && evt.Delta != null)
            {
                var d = evt.Delta;
                if (string.Equals(d.Type, "text_delta", StringComparison.Ordinal) && d.Text != null)
                {
                    contentBuilder.Append(d.Text);
                    yield return new StreamUpdate
                    {
                        Id = responseId,
                        ContentDelta = d.Text,
                        IsComplete = false
                    };
                }
                else if (string.Equals(d.Type, "input_json_delta", StringComparison.Ordinal)
                         && !string.IsNullOrEmpty(d.PartialJson)
                         && toolBlocks.TryGetValue(evt.Index.Value, out var acc))
                {
                    acc.InputJson.Append(d.PartialJson);
                }
                else if (string.Equals(d.Type, "thinking_delta", StringComparison.Ordinal)
                         && !string.IsNullOrEmpty(d.Thinking))
                {
                    yield return new StreamUpdate
                    {
                        Id = responseId,
                        ReasoningDelta = d.Thinking,
                        IsComplete = false
                    };
                }
            }

            if (string.Equals(evt.Type, "message_delta", StringComparison.Ordinal) && evt.Usage != null)
            {
                lastStreamUsage = new TokenUsage
                {
                    InputTokens = evt.Usage.InputTokens,
                    OutputTokens = evt.Usage.OutputTokens
                };
            }

            if (string.Equals(evt.Type, "message_stop", StringComparison.Ordinal))
            {
                var tools = BuildToolCallsFromAnthropicStream(toolBlocks);
                yield return new StreamUpdate
                {
                    Id = responseId,
                    IsComplete = true,
                    ToolCallDeltas = tools,
                    Usage = lastStreamUsage
                };
                streamFinalizeSent = true;
            }
        }

        if (!streamFinalizeSent)
        {
            var tools = BuildToolCallsFromAnthropicStream(toolBlocks);
            yield return new StreamUpdate
            {
                Id = responseId,
                IsComplete = true,
                ToolCallDeltas = tools,
                Usage = lastStreamUsage
            };
        }
    }

    private static List<ToolCall>? BuildToolCallsFromAnthropicStream(Dictionary<int, AnthropicStreamingToolBlock> toolBlocks)
    {
        if (toolBlocks.Count == 0)
            return null;

        var list = new List<ToolCall>(toolBlocks.Count);
        foreach (var kv in toolBlocks.OrderBy(x => x.Key))
        {
            var t = kv.Value;
            if (string.IsNullOrEmpty(t.Name))
                continue;

            var args = t.InputJson.ToString();
            if (string.IsNullOrWhiteSpace(args))
                args = "{}";
            else
            {
                try
                {
                    using var _ = JsonDocument.Parse(args);
                }
                catch (JsonException)
                {
                    args = "{}";
                }
            }

            list.Add(new ToolCall
            {
                Id = t.Id,
                Type = "function",
                Function = new FunctionCall
                {
                    Name = t.Name,
                    Arguments = args
                }
            });
        }

        return list.Count > 0 ? list : null;
    }

    private sealed class AnthropicStreamingToolBlock
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder InputJson = new();
    }

    /// <summary>
    /// 测试连接
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var testRequest = new ChatRequest
            {
                Model = _config.DefaultModel ?? "claude-3-5-haiku-20241022",
                Messages = new List<ChatMessage>
                {
                    new() { Role = ChatRole.User, Content = "Hi" }
                },
                MaxTokens = 5
            };

            await CompleteAsync(testRequest, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anthropic 连接测试失败");
            return false;
        }
    }

    #region 私有方法

    private object BuildAnthropicRequest(ChatRequest request, bool stream)
    {
        var messages = new List<object>();

        // Anthropic 的 system 是单独的字段，不在 messages 中
        // 注意：Anthropic 没有 "tool" 角色，工具调用结果必须作为 "user" 消息发送
        foreach (var msg in request.Messages)
        {
            if (msg.Role == ChatRole.System)
                continue;

            // 工具调用结果必须作为 user 消息
            var effectiveRole = msg.Role == ChatRole.Tool ? "user" : msg.Role;

            messages.Add(new
            {
                role = effectiveRole,
                content = BuildContent(msg)
            });
        }

        return new
        {
            model = request.Model,
            max_tokens = request.MaxTokens ?? 4096,
            system = request.SystemPrompt,
            messages,
            stream,
            tools = request.Tools?.Select(t => new
            {
                name = t.Function?.Name,
                description = t.Function?.Description,
                input_schema = t.Function?.Parameters
            }),
            temperature = request.Temperature,
            top_p = request.TopP
        };
    }

    private object BuildContent(ChatMessage msg)
    {
        // 工具调用结果 - Anthropic 要求作为 user 消息的一部分
        if (!string.IsNullOrEmpty(msg.ToolCallId))
        {
            return new[]
            {
                new
                {
                    type = "tool_result",
                    tool_use_id = msg.ToolCallId,
                    content = msg.Content
                }
            };
        }

        // 工具调用（助手消息）
        if (msg.ToolCalls?.Count > 0)
        {
            var contents = new List<object>();

            if (!string.IsNullOrEmpty(msg.Content))
                contents.Add(new { type = "text", text = msg.Content });

            foreach (var tc in msg.ToolCalls)
            {
                // 安全解析 Arguments（可能不是有效 JSON）
                object? inputObj = null;
                var argsStr = tc.Function?.Arguments ?? "{}";
                try
                {
                    inputObj = JsonSerializer.Deserialize<object>(argsStr);
                }
                catch (JsonException)
                {
                    // 如果不是有效 JSON，作为字符串处理
                    inputObj = argsStr;
                }

                contents.Add(new
                {
                    type = "tool_use",
                    id = tc.Id,
                    name = tc.Function?.Name,
                    input = inputObj
                });
            }

            return contents;
        }

        // 普通消息
        var parts = msg.GetEffectiveParts();
        if (parts.Count == 0)
            return new[] { new { type = "text", text = "" } };

        if (parts.Count == 1
            && string.Equals(parts[0].Type, ChatContentPart.KindText, StringComparison.OrdinalIgnoreCase))
            return new[] { new { type = "text", text = parts[0].Text ?? "" } };

        var blocks = new List<object>();
        foreach (var p in parts)
        {
            switch (p.Type?.ToLowerInvariant())
            {
                case "text":
                    blocks.Add(new { type = "text", text = p.Text ?? "" });
                    break;

                case "image":
                    if (!string.IsNullOrEmpty(p.Url))
                        blocks.Add(new { type = "image", source = new { type = "url", url = p.Url } });
                    else if (!string.IsNullOrEmpty(p.DataBase64) && !string.IsNullOrEmpty(p.MimeType))
                        blocks.Add(new { type = "image", source = new { type = "base64", media_type = p.MimeType, data = p.DataBase64 } });
                    break;

                case "file":
                case "document":
                    var mime = p.MimeType ?? "application/octet-stream";
                    var asDoc = mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Type, ChatContentPart.KindDocument, StringComparison.OrdinalIgnoreCase);
                    if (asDoc && !string.IsNullOrEmpty(p.DataBase64))
                        blocks.Add(new { type = "document", source = new { type = "base64", media_type = mime, data = p.DataBase64 } });
                    else
                        blocks.Add(new { type = "text", text = $"[Anthropic 消息 API 当前仅映射 PDF 文档块: {p.FileName ?? "file"} ({mime})]" });
                    break;

                case "input_audio":
                    blocks.Add(new { type = "text", text = $"[Anthropic Messages 需使用支持音频的模型/接口；mime={p.MimeType}]" });
                    break;
            }
        }

        return blocks.Count > 0
            ? blocks.ToArray()
            : new[] { new { type = "text", text = msg.Content } };
    }

    private ChatResponse MapResponse(AnthropicResponse response, string model)
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Assistant
        };

        var textContent = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        foreach (var block in response.Content ?? Array.Empty<AnthropicContentBlock>())
        {
            if (block.Type == "text" && block.Text != null)
            {
                textContent.Append(block.Text);
            }
            else if (block.Type == "tool_use")
            {
                toolCalls.Add(new ToolCall
                {
                    Id = block.Id ?? "",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = block.Name ?? "",
                        Arguments = JsonSerializer.Serialize(block.Input)
                    }
                });
            }
        }

        message.Content = textContent.ToString();
        if (toolCalls.Count > 0)
            message.ToolCalls = toolCalls;

        return new ChatResponse
        {
            Id = response.Id ?? "",
            Model = response.Model ?? model,
            Message = message,
            FinishReason = response.StopReason,
            Usage = new TokenUsage
            {
                InputTokens = response.Usage?.InputTokens ?? 0,
                OutputTokens = response.Usage?.OutputTokens ?? 0
            }
        };
    }

    #endregion

    #region Anthropic API 模型

    private class AnthropicResponse
    {
        public string? Id { get; set; }
        public string? Model { get; set; }
        public AnthropicContentBlock[]? Content { get; set; }
        public string? StopReason { get; set; }
        public AnthropicUsage? Usage { get; set; }
    }

    private class AnthropicContentBlock
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public object? Input { get; set; }
    }

    private class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }
        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }

    private class AnthropicStreamEvent
    {
        public string? Type { get; set; }
        public int? Index { get; set; }
        public AnthropicMessageInfo? Message { get; set; }
        [JsonPropertyName("content_block")]
        public AnthropicStreamContentBlock? ContentBlock { get; set; }
        public AnthropicDelta? Delta { get; set; }
        public AnthropicUsage? Usage { get; set; }
        [JsonPropertyName("error")]
        public AnthropicStreamErrorBody? Error { get; set; }
    }

    private class AnthropicStreamErrorBody
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
    }

    private class AnthropicStreamContentBlock
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private class AnthropicMessageInfo
    {
        public string? Id { get; set; }
    }

    private class AnthropicDelta
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        [JsonPropertyName("partial_json")]
        public string? PartialJson { get; set; }
        public string? Thinking { get; set; }
        public string? Signature { get; set; }
    }

    #endregion
}