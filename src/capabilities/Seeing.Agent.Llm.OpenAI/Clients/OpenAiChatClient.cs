using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.OpenAI.Clients;

/// <summary>
/// OpenAI Chat Completions API 客户端（原生 HTTP 实现，<see cref="IChatCompletionClient"/>）。
/// 直接解析 SSE 流，正确提取 choices[0].delta.reasoning_content。
/// <para>可选实现 <see cref="IModelCatalogClient"/>：当 <c>ProviderConfig.ListModels = true</c>
/// 时由 <c>ConfiguredLlmProvider</c> 合并远端 <c>GET {baseURL}/models</c>。</para>
/// </summary>
public class OpenAiChatClient : IChatCompletionClient, IModelCatalogClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;
    private readonly ILlmCallInterceptorRegistry? _interceptorRegistry;
    private readonly bool _ownsHttpClient;
    private readonly OpenAiModelsClient _modelsClient;

    public string ProviderId => _config.Id;
    public string ProviderType => ProviderTypes.OpenAi;

    public OpenAiChatClient(
        ProviderConfig config,
        HttpClient httpClient,
        ILogger logger,
        ILlmCallInterceptorRegistry? interceptorRegistry = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(httpClient);
        _interceptorRegistry = interceptorRegistry;

        // 允许匿名网关（如 OpenCode Zen 免费模型）：ApiKey 与 Authorization 头均可缺省，
        // 缺失时直接不发送 Authorization 头（由 OpenAiHttpHelper.ConfigureHttpClient 处理）。

        // 保留调用方传入的 handler（包括 Provider 专用代理）；
        // 单测等已配置 BaseAddress 的 HttpClient 也继续直接复用。
        // 已配置 BaseAddress 的共享 HttpClient 不被拥有（Dispose 时不释放）；
        // 工厂新建的 HttpClient 在此配置并由客户端拥有（须在配置前判定所有权）。
        var ownsClient = httpClient.BaseAddress == null;
        _httpClient = ownsClient
            ? ConfigureFactoryClient(httpClient, config, logger)
            : httpClient;
        _ownsHttpClient = ownsClient;
        _modelsClient = new OpenAiModelsClient(_config, _httpClient, _logger);
    }

    /// <inheritdoc />
    Task<IReadOnlyList<ModelConfig>> IModelCatalogClient.ListModelsAsync(
        CancellationToken cancellationToken)
        => _modelsClient.ListModelsAsync(cancellationToken);

    /// <summary>
    /// 释放自建 HttpClient（共享客户端不释放）。
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private IReadOnlyList<ILlmCallInterceptor> ResolveInterceptors()
        => _interceptorRegistry?.Resolve(ProviderId, ProviderType)
           ?? Array.Empty<ILlmCallInterceptor>();

    private static HttpClient ConfigureFactoryClient(HttpClient httpClient, ProviderConfig config, ILogger logger)
    {
        OpenAiHttpHelper.ConfigureHttpClient(httpClient, config, logger);
        return httpClient;
    }

    public async Task<ChatResponse> CompleteAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        _logger.LogDebug("ChatCompletions 非流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: false);
        using var response = await OpenAiHttpHelper.PostJsonAsync(
            _httpClient, "chat/completions", body, _logger, ct, _config, call, ResolveInterceptors());
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var completion = OpenAiHttpHelper.Deserialize<ChatCompletionResponse>(json, _logger);
        return MapResponse(completion!, request.Model);
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug("ChatCompletions 流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: true);
        using var response = await OpenAiHttpHelper.PostJsonAsync(
            _httpClient, "chat/completions", body, _logger, ct, _config, call, ResolveInterceptors());
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

    public async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        // 决议 15/25：本客户端只服务 Chat 能力；连接测试维持既有真实探针——发一次 5 token 补全
        if (!capability.HasFlag(LlmCapabilityKind.Chat))
        {
            _logger.LogWarning("OpenAiChatClient 只服务 Chat 能力，收到 {Capability} 连接测试请求", capability);
            return false;
        }

        try
        {
            var req = new ChatRequest
            {
                Model = modelId,
                Messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "Hi" } },
                MaxTokens = 5
            };
            await CompleteAsync(req, call, ct);
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

        // 流式请求声明 include_usage：官方 API 默认不发送 usage chunk，
        // 缺失会导致 TokenBudget 回退字符估算（预算/压缩阈值失真）
        if (stream)
            body.StreamOptions = new ChatCompletionStreamOptions { IncludeUsage = true };

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

        if (!string.IsNullOrWhiteSpace(request.ThinkingEffort))
        {
            if (ThinkingEffortKeys.IsOff(request.ThinkingEffort))
            {
                body.Thinking = new ChatCompletionThinking { Type = "disabled" };
            }
            else
            {
                body.Thinking = new ChatCompletionThinking { Type = "enabled" };
                body.ReasoningEffort = request.ThinkingEffort;
            }
        }
        else
        {
            //默认开启思考，但是不设置思考强度
            body.Thinking = new ChatCompletionThinking { Type = "enabled" };
        }

        return body;
    }

    internal List<ChatCompletionMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<ChatCompletionMessage>();
        var echoReasoning = request.EchoReasoningContent;

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ChatCompletionMessage
            {
                Role = "system",
                Content = request.SystemPrompt
            });
        }

        // Chat Completions 的 role:"tool" 只允许 text content（官方：only type `text` is supported），
        // 且 assistant(tool_calls) 之后的 tool 消息必须**连续**（tool, tool, user ✅ / tool, user, tool ❌ 400）。
        // 所以工具产出图片不能在 tool 消息里带出，也不能逐条插入 user：
        // 先缓冲，等连续 tool 段结束（遇到非 tool 消息或列表结束）再聚合成**一条** user 消息。
        var pendingToolImages = new List<ChatCompletionContentPart>();

        void FlushPendingToolImages()
        {
            if (pendingToolImages.Count == 0)
                return;

            messages.Add(new ChatCompletionMessage
            {
                Role = "user",
                Content = pendingToolImages.ToList()
            });
            pendingToolImages.Clear();
        }

        foreach (var msg in request.Messages)
        {
            if (!string.Equals(msg.Role, ChatRole.Tool, StringComparison.OrdinalIgnoreCase))
                FlushPendingToolImages();

            if (string.Equals(msg.Role, ChatRole.Tool, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new ChatCompletionMessage
                {
                    Role = "tool",
                    Content = msg.Content,
                    ToolCallId = msg.ToolCallId
                });

                CollectToolImages(msg, pendingToolImages);
                continue;
            }

            messages.Add(msg.Role switch
            {
                ChatRole.User => BuildUserMessage(msg),
                ChatRole.Assistant => BuildAssistantMessage(msg, echoReasoning),
                _ => BuildUserMessage(msg)
            });
        }

        FlushPendingToolImages();

        return messages;
    }

    /// <summary>
    /// 把 tool 消息里非文本内容段转成 <c>image_url</c> 片段，暂存待聚合。
    /// <para>文本不在此收集：tool 消息的文本已由 <c>Content</c> 承载，重复会浪费 token。</para>
    /// </summary>
    private static void CollectToolImages(ChatMessage msg, List<ChatCompletionContentPart> target)
    {
        if (msg.Parts is not { Count: > 0 })
            return;

        foreach (var part in msg.Parts)
        {
            if (!string.Equals(part.Type, ChatContentPart.KindImage, StringComparison.OrdinalIgnoreCase))
                continue;

            var url = !string.IsNullOrEmpty(part.Url)
                ? part.Url
                : !string.IsNullOrEmpty(part.DataBase64) && !string.IsNullOrEmpty(part.MimeType)
                    ? $"data:{part.MimeType};base64,{part.DataBase64}"
                    : null;

            if (string.IsNullOrEmpty(url))
                continue;

            target.Add(new ChatCompletionContentPart
            {
                Type = "image_url",
                ImageUrl = new ChatCompletionImageUrl
                {
                    Url = url,
                    Detail = part.ImageDetail?.ToLowerInvariant() switch
                    {
                        "low" => "low",
                        "high" => "high",
                        _ => "auto"
                    }
                }
            });
        }
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

    private static ChatCompletionMessage BuildAssistantMessage(ChatMessage msg, bool echoReasoning)
    {
        var reasoning = echoReasoning && !string.IsNullOrEmpty(msg.ReasoningContent)
            ? msg.ReasoningContent
            : null;

        if (msg.ToolCalls?.Count > 0)
        {
            return new ChatCompletionMessage
            {
                Role = "assistant",
                Content = msg.Content,
                ReasoningContent = reasoning,
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
        return new ChatCompletionMessage
        {
            Role = "assistant",
            Content = msg.Content,
            ReasoningContent = reasoning
        };
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

                // 对齐 Anthropic 的 Parse 校验：分片拼出的非法 JSON 参数回退 "{}"，避免下游解析崩溃
                var args = kv.Value.Args.Length > 0 ? kv.Value.Args.ToString() : "{}";
                if (!string.IsNullOrWhiteSpace(args))
                {
                    try { using var _ = JsonDocument.Parse(args); }
                    catch (JsonException) { args = "{}"; }
                }
                else
                {
                    args = "{}";
                }

                list.Add(new ToolCall
                {
                    Id = kv.Value.Id,
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = kv.Value.Name,
                        Arguments = args
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
