using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// OpenAI Responses API 客户端（原生 HTTP 实现）。
/// 发送到 POST /responses，解析 event: + data: 双行 SSE。
/// 映射到 ILlmClient 的统一 StreamUpdate / ChatResponse 接口。
/// </summary>
public class OpenAiResponsesClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;

    public string ProviderId => _config.Id;
    public ProviderType ProviderType => ProviderType.OpenAI;

    public OpenAiResponsesClient(ProviderConfig config, HttpClient httpClient, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 允许匿名网关：ApiKey 与 Authorization 头均可缺省，缺失时直接不发送认证头

        // 复用工厂传入的 HttpClient，不能在这里 new HttpClient()，否则会绕过 Provider 代理。
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
        _logger.LogDebug("ResponsesAPI 非流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: false);
        var response = await OpenAiHttpHelper.PostJsonAsync(_httpClient, "responses", body, _logger, ct);
        await OpenAiHttpHelper.EnsureSuccessAsync(response, _logger, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var completion = OpenAiHttpHelper.Deserialize<ResponsesStreamEvent>(json, _logger);
        return MapResponse(completion!, request.Model);
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug("ResponsesAPI 流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: true);
        var response = await OpenAiHttpHelper.PostJsonAsync(_httpClient, "responses", body, _logger, ct);
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
            input.Add(new ResponsesInputItem
            {
                Role = msg.Role switch
                {
                    ChatRole.Tool => "assistant", // Responses API 工具调用结果包含在 assistant 消息里
                    _ => msg.Role
                },
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
