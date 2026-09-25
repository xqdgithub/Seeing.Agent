using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.OpenAI.Clients;

/// <summary>
/// OpenAI Responses API 客户端（原生 HTTP 实现）。
/// 发送到 POST /responses，解析 event: + data: 双行 SSE。
/// 映射到 ILlmClient 的统一 StreamUpdate / ChatResponse 接口。
/// </summary>
public class OpenAiResponsesClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;
    private readonly ILlmCallInterceptorRegistry? _interceptorRegistry;
    private readonly bool _ownsHttpClient;

    public string ProviderId => _config.Id;
    public string ProviderType => ProviderTypes.OpenAi;

    public OpenAiResponsesClient(
        ProviderConfig config,
        HttpClient httpClient,
        ILogger logger,
        ILlmCallInterceptorRegistry? interceptorRegistry = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(httpClient);
        _interceptorRegistry = interceptorRegistry;

        // 允许匿名网关：ApiKey 与 Authorization 头均可缺省，缺失时直接不发送认证头

        // 复用工厂传入的 HttpClient，不能在这里 new HttpClient()，否则会绕过 Provider 代理。
        // 已配置 BaseAddress 的共享 HttpClient 不被拥有（Dispose 时不释放）；
        // 工厂新建的 HttpClient 在此配置并由客户端拥有（须在配置前判定所有权）。
        var ownsClient = httpClient.BaseAddress == null;
        _httpClient = ownsClient
            ? ConfigureFactoryClient(httpClient, config, logger)
            : httpClient;
        _ownsHttpClient = ownsClient;
    }

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
        _logger.LogDebug("ResponsesAPI 非流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: false);
        using var response = await OpenAiHttpHelper.PostJsonAsync(
            _httpClient, "responses", body, _logger, ct, _config, call, ResolveInterceptors());
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var completion = OpenAiHttpHelper.Deserialize<ResponsesStreamEvent>(json, _logger);
        return MapResponse(completion!, request.Model);
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug("ResponsesAPI 流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: true);
        using var response = await OpenAiHttpHelper.PostJsonAsync(
            _httpClient, "responses", body, _logger, ct, _config, call, ResolveInterceptors());
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);

        var responseId = string.Empty;
        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var pendingToolCalls = new Dictionary<string, ToolCallAccumulator>(); // keyed by call_id
        var streamFinalizeSent = false;
        TokenUsage? lastUsage = null;

        await foreach (var evt in OpenAiHttpHelper.ReadResponsesSseAsync(stream, _logger, ct))
        {
            // 提取 response ID
            if (string.IsNullOrEmpty(responseId) && !string.IsNullOrEmpty(evt.Response?.Id))
                responseId = evt.Response.Id;

            switch (evt.Type)
            {
                case "response.output_text.delta":
                    if (!string.IsNullOrEmpty(evt.Delta))
                    {
                        contentBuilder.Append(evt.Delta);
                        yield return new StreamUpdate
                        {
                            Id = responseId,
                            ContentDelta = evt.Delta,
                            IsComplete = false
                        };
                    }
                    break;

                case "response.reasoning_summary_text.delta":
                    if (!string.IsNullOrEmpty(evt.Delta))
                    {
                        reasoningBuilder.Append(evt.Delta);
                        yield return new StreamUpdate
                        {
                            Id = responseId,
                            ReasoningDelta = evt.Delta,
                            IsComplete = false
                        };
                    }
                    break;

                case "response.function_call_arguments.delta":
                    if (!string.IsNullOrEmpty(evt.CallId) && !string.IsNullOrEmpty(evt.Delta))
                    {
                        if (!pendingToolCalls.TryGetValue(evt.CallId, out var acc))
                        {
                            acc = new ToolCallAccumulator();
                            pendingToolCalls[evt.CallId] = acc;
                        }
                        acc.Arguments.Append(evt.Delta);
                    }
                    break;

                case "response.function_call_arguments.done":
                    if (!string.IsNullOrEmpty(evt.CallId))
                    {
                        if (!pendingToolCalls.TryGetValue(evt.CallId, out var acc))
                            acc = pendingToolCalls[evt.CallId] = new ToolCallAccumulator();

                        acc.Name = evt.Name ?? "";
                        acc.Arguments = new StringBuilder(evt.Arguments ?? "{}");
                    }
                    break;

                case "response.completed":
                    if (evt.Response?.Usage != null)
                    {
                        var u = evt.Response.Usage;
                        lastUsage = new TokenUsage
                        {
                            InputTokens = u.InputTokens,
                            OutputTokens = u.OutputTokens,
                            ReasoningTokens = u.OutputTokensDetails?.ReasoningTokens ?? 0
                        };
                    }

                    yield return new StreamUpdate
                    {
                        Id = responseId,
                        IsComplete = true,
                        ToolCallDeltas = BuildToolCallsFromAccumulators(pendingToolCalls),
                        Usage = lastUsage
                    };
                    streamFinalizeSent = true;
                    break;
            }
        }

        if (!streamFinalizeSent)
        {
            yield return new StreamUpdate
            {
                Id = responseId,
                IsComplete = true,
                ToolCallDeltas = BuildToolCallsFromAccumulators(pendingToolCalls),
                Usage = lastUsage
            };
        }
    }

    public async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
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
            _logger.LogError(ex, "ResponsesAPI 连接测试失败");
            return false;
        }
    }

    #region 请求构建

    private ResponsesRequest BuildRequest(ChatRequest request, bool stream)
    {
        var input = new List<ResponsesInputItem>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            input.Add(new ResponsesInputItem { Role = "system", Content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            // 工具调用结果 → function_call_output item（以 call_id 关联）
            // 旧实现把 tool 消息伪装成 assistant 文本，第二轮起工具调用关联断裂
            if (msg.Role == ChatRole.Tool)
            {
                input.Add(new ResponsesInputItem
                {
                    Type = "function_call_output",
                    CallId = msg.ToolCallId ?? "",
                    Output = msg.Content
                });
                continue;
            }

            // assistant 消息带工具调用 → 每个调用映射为 function_call input item
            // （输出侧 MapResponse 已支持 function_call，此处补齐输入侧对称映射）
            if (msg.Role == ChatRole.Assistant && msg.ToolCalls?.Count > 0)
            {
                if (!string.IsNullOrEmpty(msg.Content))
                    input.Add(new ResponsesInputItem { Role = "assistant", Content = msg.Content });

                foreach (var tc in msg.ToolCalls)
                {
                    input.Add(new ResponsesInputItem
                    {
                        Type = "function_call",
                        CallId = tc.Id,
                        Name = tc.Function?.Name ?? "",
                        Arguments = tc.Function?.Arguments ?? "{}"
                    });
                }
                continue;
            }

            input.Add(new ResponsesInputItem
            {
                Role = msg.Role,
                Content = msg.Content
            });
        }

        var body = new ResponsesRequest
        {
            Model = request.Model,
            Input = input,
            Stream = stream,
            MaxOutputTokens = request.MaxTokens ?? 4096,
            Temperature = request.Temperature,
            TopP = request.TopP
        };

        if (request.Tools?.Count > 0)
        {
            body.Tools = request.Tools.Select(t => new ResponsesTool
            {
                Type = "function",
                Name = t.Function?.Name ?? "",
                Description = t.Function?.Description,
                Parameters = t.Function?.Parameters
            }).ToList();
        }

        return body;
    }

    #endregion

    #region 响应映射

    /// <summary>
    /// 非流式 Responses API 响应：直接从 output 数组还原消息、推理、工具调用
    /// </summary>
    private static ChatResponse MapResponse(ResponsesStreamEvent evt, string model)
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Assistant
        };

        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        var output = evt.Response?.Output;
        if (output != null)
        {
            foreach (var block in output)
            {
                switch (block.Type)
                {
                    case "message":
                        if (block.Content != null)
                        {
                            foreach (var part in block.Content)
                            {
                                if (part is { Type: "output_text", Text: not null })
                                    contentBuilder.Append(part.Text);
                            }
                        }
                        break;

                    case "reasoning":
                        if (block.Content != null)
                        {
                            foreach (var part in block.Content)
                            {
                                if (part is { Type: "reasoning_summary_text", Text: not null })
                                    reasoningBuilder.Append(part.Text);
                            }
                        }
                        break;

                    case "function_call":
                        toolCalls.Add(new ToolCall
                        {
                            Id = block.CallId ?? "",
                            Type = "function",
                            Function = new FunctionCall
                            {
                                Name = block.Name ?? "",
                                Arguments = block.Arguments ?? "{}"
                            }
                        });
                        break;
                }
            }
        }

        message.Content = contentBuilder.ToString();
        if (reasoningBuilder.Length > 0)
            message.ReasoningContent = reasoningBuilder.ToString();
        if (toolCalls.Count > 0)
            message.ToolCalls = toolCalls;

        return new ChatResponse
        {
            Id = evt.Response?.Id ?? "",
            Model = evt.Response?.Model ?? model,
            Message = message,
            FinishReason = evt.Response?.Status == "completed" ? "stop" : null,
            Usage = MapUsage(evt.Response?.Usage)
        };
    }

    private static TokenUsage? MapUsage(OpenAiResponsesUsage? u)
    {
        if (u == null) return null;
        return new TokenUsage
        {
            InputTokens = u.InputTokens,
            OutputTokens = u.OutputTokens,
            ReasoningTokens = u.OutputTokensDetails?.ReasoningTokens ?? 0
        };
    }

    private static List<ToolCall>? BuildToolCallsFromAccumulators(
        Dictionary<string, ToolCallAccumulator> accumulators)
    {
        if (accumulators.Count == 0) return null;
        var list = new List<ToolCall>();
        foreach (var kv in accumulators)
        {
            if (string.IsNullOrEmpty(kv.Value.Name)) continue;
            var args = kv.Value.Arguments.ToString();
            if (string.IsNullOrWhiteSpace(args)) args = "{}";
            else
            {
                try { using var _ = JsonDocument.Parse(args); }
                catch (JsonException) { args = "{}"; }
            }

            list.Add(new ToolCall
            {
                Id = kv.Key,
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

    #endregion

    private sealed class ToolCallAccumulator
    {
        public string Name = "";
        public StringBuilder Arguments = new();
    }
}
