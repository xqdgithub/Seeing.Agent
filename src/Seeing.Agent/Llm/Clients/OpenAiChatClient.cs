using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// OpenAI Chat Completions API 客户端（原生 HTTP 实现）。
/// 直接解析 SSE 流，正确提取 choices[0].delta.reasoning_content。
/// </summary>
public class OpenAiChatClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;

    public string ProviderId => _config.Id;
    public ProviderType ProviderType => ProviderType.OpenAI;

    public OpenAiChatClient(ProviderConfig config, HttpClient httpClient, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(httpClient);

        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("ApiKey is required", nameof(config));

        // 保留调用方传入的 handler（包括 Provider 专用代理）；
        // 单测等已配置 BaseAddress 的 HttpClient 也继续直接复用。
        _httpClient = httpClient.BaseAddress != null
            ? httpClient
            : ConfigureFactoryClient(httpClient, config, logger);
    }

    private static HttpClient ConfigureFactoryClient(HttpClient httpClient, ProviderConfig config, ILogger logger)
    {
        OpenAiHttpHelper.ConfigureHttpClient(httpClient, config, logger);
        return httpClient;
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("ChatCompletions 非流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: false);
        var response = await OpenAiHttpHelper.PostJsonAsync(_httpClient, "chat/completions", body, _logger, ct);
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var completion = OpenAiHttpHelper.Deserialize<ChatCompletionResponse>(json, _logger);
        return MapResponse(completion!, request.Model);
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug("ChatCompletions 流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: true);
        var response = await OpenAiHttpHelper.PostJsonAsync(_httpClient, "chat/completions", body, _logger, ct);
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        _logger.LogInformation("ChatCompletions SSE 已建立: StatusCode={StatusCode}", (int)response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(ct);

        var responseId = string.Empty;
        var streamingTools = new StreamingToolCallAccumulator();
        string? pendingFinishReason = null;
        TokenUsage? lastUsage = null;
        var chunkCount = 0;
        var reasoningCount = 0;
        var contentCount = 0;

        await foreach (var chunk in OpenAiHttpHelper.ReadChatCompletionsSseAsync(stream, _logger, ct))
        {
            chunkCount++;
            if (string.IsNullOrEmpty(responseId) && !string.IsNullOrEmpty(chunk.Id))
                responseId = chunk.Id;

            var delta = chunk.Choices?.FirstOrDefault()?.Delta;
            var finishReason = chunk.Choices?.FirstOrDefault()?.FinishReason;

            // 任意 chunk 都可携带 usage（含 finish_reason 之后的 trailing usage）
            if (chunk.Usage != null)
                lastUsage = MapUsage(chunk.Usage);

            // 每个 chunk 输出 Debug 级别摘要
            _logger.LogDebug("SSE chunk #{N}: hasDelta={HasDelta}, content={C}, reasoning={R}, toolCalls={T}, finish={F}",
                chunkCount, delta != null,
                delta?.Content?.Length.ToString() ?? "-",
                delta?.ReasoningContent?.Length.ToString() ?? "-",
                delta?.ToolCalls?.Count.ToString() ?? "-",
                finishReason ?? "-");

            if (delta != null)
            {
                if (!string.IsNullOrEmpty(delta.ReasoningContent))
                {
                    reasoningCount++;
                    yield return new StreamUpdate
                    {
                        Id = responseId,
                        ReasoningDelta = delta.ReasoningContent,
                        IsComplete = false
                    };
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    contentCount++;
                    yield return new StreamUpdate
                    {
                        Id = responseId,
                        ContentDelta = delta.Content,
                        IsComplete = false
                    };
                }

                if (delta.ToolCalls != null)
                {
                    foreach (var tc in delta.ToolCalls)
                        streamingTools.Append(tc);
                }
            }

            // 只记录 finish_reason，推迟到 SSE 真正结束后再发 IsComplete
            if (!string.IsNullOrEmpty(finishReason))
            {
                pendingFinishReason = finishReason;
                _logger.LogInformation(
                    "ChatCompletions SSE 收到结束原因: FinishReason={FinishReason}, Chunks={N}, Reasoning={R}, Content={C}",
                    finishReason, chunkCount, reasoningCount, contentCount);
            }
        }

        if (pendingFinishReason == null)
        {
            _logger.LogWarning(
                "ChatCompletions SSE 未收到 finish_reason 即结束: Chunks={N}, Reasoning={R}, Content={C}",
                chunkCount, reasoningCount, contentCount);
        }
        else
        {
            _logger.LogInformation(
                "ChatCompletions SSE 流结束: FinishReason={FinishReason}, Chunks={N}, Reasoning={R}, Content={C}",
                pendingFinishReason, chunkCount, reasoningCount, contentCount);
        }

        yield return new StreamUpdate
        {
            Id = responseId,
            IsComplete = true,
            FinishReason = pendingFinishReason,
            ToolCallDeltas = streamingTools.Build(),
            Usage = lastUsage
        };
    }

    public async Task<bool> TestConnectionAsync(string modelId, CancellationToken ct = default)
    {
        try
        {
            var req = new ChatRequest
            {
                Model = modelId,
                Messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "Hi" } },
                MaxTokens = 5
            };
            await CompleteAsync(req, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChatCompletions 连接测试失败");
            return false;
        }
    }

    #region 请求构建

    private ChatCompletionRequest BuildRequest(ChatRequest request, bool stream)
    {
        var body = new ChatCompletionRequest
        {
            Model = request.Model,
            Stream = stream,
            MaxTokens = request.MaxTokens ?? 4096,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Messages = BuildMessages(request)
        };

        if (request.Tools?.Count > 0)
        {
            body.Tools = request.Tools.Select(t => new ChatCompletionTool
            {
                Type = "function",
                Function = new ChatCompletionFunction
                {
                    Name = t.Function?.Name ?? "",
                    Description = t.Function?.Description,
                    Parameters = t.Function?.Parameters
                }
            }).ToList();
        }

        return body;
    }

    private List<ChatCompletionMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<ChatCompletionMessage>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ChatCompletionMessage
            {
                Role = "system",
                Content = request.SystemPrompt
            });
        }

        foreach (var msg in request.Messages)
        {
            messages.Add(msg.Role switch
            {
                ChatRole.User => BuildUserMessage(msg),
                ChatRole.Assistant => BuildAssistantMessage(msg),
                ChatRole.Tool => new ChatCompletionMessage
                {
                    Role = "tool",
                    Content = msg.Content,
                    ToolCallId = msg.ToolCallId
                },
                _ => BuildUserMessage(msg)
            });
        }

        return messages;
    }

    private ChatCompletionMessage BuildUserMessage(ChatMessage msg)
    {
        var parts = msg.GetEffectiveParts();
        if (parts.Count == 0)
            return new ChatCompletionMessage { Role = "user", Content = "" };

        // 简单文本消息
        if (parts.Count == 1
            && string.Equals(parts[0].Type, ChatContentPart.KindText, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(parts[0].Text))
        {
            return new ChatCompletionMessage { Role = "user", Content = parts[0].Text };
        }

        // 多模态消息
        var contentParts = new List<ChatCompletionContentPart>();
        foreach (var p in parts)
        {
            switch (p.Type?.ToLowerInvariant())
            {
                case "text":
                    if (!string.IsNullOrEmpty(p.Text))
                        contentParts.Add(new ChatCompletionContentPart { Type = "text", Text = p.Text });
                    break;

                case "image":
                    contentParts.Add(new ChatCompletionContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ChatCompletionImageUrl
                        {
                            Url = !string.IsNullOrEmpty(p.Url)
                                ? p.Url
                                : !string.IsNullOrEmpty(p.DataBase64) && !string.IsNullOrEmpty(p.MimeType)
                                    ? $"data:{p.MimeType};base64,{p.DataBase64}"
                                    : "",
                            Detail = p.ImageDetail?.ToLowerInvariant() switch
                            {
                                "low" => "low",
                                "high" => "high",
                                _ => "auto"
                            }
                        }
                    });
                    break;

                case "file":
                case "document":
                case "input_audio":
                    _logger.LogWarning("ChatCompletions HTTP 暂不支持 {Type} 内容块", p.Type);
                    break;
            }
        }

        return contentParts.Count == 0
            ? new ChatCompletionMessage { Role = "user", Content = msg.Content }
            : new ChatCompletionMessage { Role = "user", Content = contentParts };
    }

    private static ChatCompletionMessage BuildAssistantMessage(ChatMessage msg)
    {
        if (msg.ToolCalls?.Count > 0)
        {
            return new ChatCompletionMessage
            {
                Role = "assistant",
                Content = msg.Content,
                ToolCalls = msg.ToolCalls.Select(tc => new ChatCompletionToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    Function = new ChatCompletionToolCallFunction
                    {
                        Name = tc.Function?.Name ?? "",
                        Arguments = tc.Function?.Arguments ?? "{}"
                    }
                }).ToList()
            };
        }
        return new ChatCompletionMessage { Role = "assistant", Content = msg.Content };
    }

    #endregion

    #region 响应映射

    private static ChatResponse MapResponse(ChatCompletionResponse completion, string model)
    {
        var choice = completion.Choices?.FirstOrDefault();
        var msg = choice?.Message;

        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = msg?.Content ?? "",
            ReasoningContent = msg?.ReasoningContent
        };

        if (msg?.ToolCalls?.Count > 0)
        {
            message.ToolCalls = msg.ToolCalls
                .Where(tc => string.Equals(tc.Type, "function", StringComparison.OrdinalIgnoreCase))
                .Select(tc => new ToolCall
                {
                    Id = tc.Id ?? "",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = tc.Function?.Name ?? "",
                        Arguments = tc.Function?.Arguments ?? "{}"
                    }
                }).ToList();
        }

        return new ChatResponse
        {
            Id = completion.Id ?? "",
            Model = completion.Model ?? model,
            Message = message,
            FinishReason = choice?.FinishReason,
            Usage = MapUsage(completion.Usage)
        };
    }

    private static TokenUsage? MapUsage(OpenAiUsage? u)
    {
        if (u == null) return null;
        return new TokenUsage
        {
            InputTokens = u.PromptTokens,
            OutputTokens = u.CompletionTokens,
            ReasoningTokens = u.CompletionTokensDetails?.ReasoningTokens ?? 0
        };
    }

    #endregion

    #region 流式工具调用聚合

    private sealed class StreamingToolCallAccumulator
    {
        private readonly Dictionary<int, ToolSlot> _byIndex = new();

        public void Append(ChatCompletionChunkToolCall tc)
        {
            if (!_byIndex.TryGetValue(tc.Index, out var slot))
            {
                slot = new ToolSlot();
                _byIndex[tc.Index] = slot;
            }

            if (!string.IsNullOrEmpty(tc.Id))
                slot.Id = tc.Id;
            if (!string.IsNullOrEmpty(tc.Function?.Name))
                slot.Name = tc.Function.Name;
            if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                slot.Args.Append(tc.Function.Arguments);
        }

        public List<ToolCall>? Build()
        {
            if (_byIndex.Count == 0) return null;
            var list = new List<ToolCall>();
            foreach (var kv in _byIndex.OrderBy(x => x.Key))
            {
                if (string.IsNullOrEmpty(kv.Value.Name)) continue;
                list.Add(new ToolCall
                {
                    Id = kv.Value.Id,
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = kv.Value.Name,
                        Arguments = kv.Value.Args.Length > 0 ? kv.Value.Args.ToString() : "{}"
                    }
                });
            }
            return list.Count > 0 ? list : null;
        }

        private sealed class ToolSlot
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new();
        }
    }

    #endregion
}
