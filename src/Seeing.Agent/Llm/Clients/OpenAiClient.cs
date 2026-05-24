using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CoreChatMessage = Seeing.Agent.Llm.ChatMessage;
// 使用类型别名解决命名冲突
using SdkChatMessage = OpenAI.Chat.ChatMessage;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// OpenAI 客户端 - 使用官方 SDK 发送请求
/// 只负责发送请求和接收响应，不负责模型定义
/// </summary>
public class OpenAiClient : ILlmClient
{
    private readonly OpenAIClient _openAiClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;

    /// <summary>Provider ID</summary>
    public string ProviderId => _config.Id;

    /// <summary>Provider 类型</summary>
    public ProviderType ProviderType => ProviderType.OpenAI;

    /// <summary>
    /// 创建 OpenAI 客户端
    /// </summary>
    public OpenAiClient(ProviderConfig config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("ApiKey is required", nameof(config));

        // 配置客户端选项
        var options = new OpenAI.OpenAIClientOptions();
        if (!string.IsNullOrEmpty(config.BaseUrl))
        {
            options.Endpoint = new Uri(config.BaseUrl);
        }

        _openAiClient = new OpenAIClient(new ApiKeyCredential(config.ApiKey), options);

        _logger.LogDebug("OpenAI 客户端已初始化: {ProviderId}, BaseUrl={BaseUrl}",
            ProviderId, config.BaseUrl ?? "https://api.openai.com/v1");
    }

    /// <summary>
    /// 发送聊天补全请求
    /// </summary>
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("OpenAI 请求: Model={Model}, Messages={Count}", request.Model, request.Messages.Count);

        var chatClient = ResolveChatClient(request);
        var messages = BuildMessages(request);
        var options = BuildOptions(request);

        var response = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

        return MapResponse(response.Value, request.Model);
    }

    /// <summary>
    /// 发送流式聊天补全请求
    /// </summary>
    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("OpenAI 流式请求: Model={Model}", request.Model);

        var chatClient = ResolveChatClient(request);
        var messages = BuildMessages(request);
        var options = BuildOptions(request);

        var responseId = string.Empty;
        var contentBuilder = new StringBuilder();
        var streamingTools = new StreamingToolCallAccumulator();

        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            if (string.IsNullOrEmpty(responseId))
                responseId = update.CompletionId;

            foreach (var contentPart in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(contentPart.Text))
                {
                    contentBuilder.Append(contentPart.Text);
                    yield return new StreamUpdate
                    {
                        Id = responseId,
                        ContentDelta = contentPart.Text,
                        IsComplete = false
                    };
                }
            }

            foreach (var toolCallUpdate in update.ToolCallUpdates)
                streamingTools.Append(toolCallUpdate);

            if (update.FinishReason != null)
            {
                var usage = update.Usage;
                // 部分模型在 function calling 结束时仍报告 Stop，故只要有聚合结果就下发
                var built = streamingTools.BuildFunctionToolCalls();
                var finalTools = built.Count > 0 ? built : null;

                yield return new StreamUpdate
                {
                    Id = responseId,
                    IsComplete = true,
                    ToolCallDeltas = finalTools,
                    Usage = usage != null ? new TokenUsage
                    {
                        InputTokens = usage.InputTokenCount,
                        OutputTokens = usage.OutputTokenCount
                    } : null
                };
            }
        }
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
                Model = _config.DefaultModel ?? "gpt-4o-mini",
                Messages = new List<CoreChatMessage>
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
            _logger.LogError(ex, "OpenAI 连接测试失败");
            return false;
        }
    }

    #region 私有方法

    /// <summary>
    /// 按请求中的模型 ID 解析 ChatClient（SDK 2.x 在构造时绑定模型，非 ChatCompletionOptions）
    /// </summary>
    private ChatClient ResolveChatClient(ChatRequest request)
    {
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? (_config.DefaultModel ?? "gpt-4o")
            : request.Model.Trim();
        return _openAiClient.GetChatClient(model);
    }

    private List<SdkChatMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<SdkChatMessage>();

        // 系统消息
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new SystemChatMessage(request.SystemPrompt));
        }

        // 对话消息
        foreach (var msg in request.Messages)
        {
            messages.Add(msg.Role switch
            {
                ChatRole.User => BuildUserCoreMessage(msg),
                ChatRole.Assistant => BuildAssistantMessage(msg),
                ChatRole.Tool => new ToolChatMessage(msg.ToolCallId ?? "", msg.Content),
                _ => BuildUserCoreMessage(msg)
            });
        }

        return messages;
    }

    private SdkChatMessage BuildUserCoreMessage(CoreChatMessage msg)
    {
        var parts = msg.GetEffectiveParts();
        if (parts.Count == 0)
            return new UserChatMessage("");

        if (parts.Count == 1
            && string.Equals(parts[0].Type, ChatContentPart.KindText, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(parts[0].Text))
            return new UserChatMessage(parts[0].Text!);

        var sdkParts = new List<ChatMessageContentPart>();
        foreach (var p in parts)
        {
            try
            {
                AppendOpenAiUserPart(sdkParts, p);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "忽略无效的用户内容段 type={Type}", p.Type);
            }
        }

        return sdkParts.Count == 0
            ? new UserChatMessage(msg.Content)
            : new UserChatMessage(sdkParts);
    }

    private void AppendOpenAiUserPart(List<ChatMessageContentPart> sdkParts, ChatContentPart p)
    {
        switch (p.Type?.ToLowerInvariant())
        {
            case "text":
                if (!string.IsNullOrEmpty(p.Text))
                    sdkParts.Add(ChatMessageContentPart.CreateTextPart(p.Text));
                return;

            case "image":
                var detail = MapOpenAiImageDetail(p.ImageDetail);
                if (!string.IsNullOrEmpty(p.Url))
                {
                    sdkParts.Add(ChatMessageContentPart.CreateImagePart(new Uri(p.Url), detail));
                    return;
                }

                if (!string.IsNullOrEmpty(p.DataBase64) && !string.IsNullOrEmpty(p.MimeType))
                {
                    sdkParts.Add(ChatMessageContentPart.CreateImagePart(
                        BinaryData.FromBytes(Convert.FromBase64String(p.DataBase64)),
                        p.MimeType,
                        detail));
                }

                return;

            case "file":
            case "document":
                // OpenAI SDK 2.0.0 的 ChatMessageContentPart 不保证 CreateFilePart 可用，当前实现选择跳过文件内容
                return;

            case "input_audio":
                // OpenAI SDK 2.0.0 不保证音频输入部分的实现，跳过非文本内容
                return;

            default:
                _logger.LogWarning("未知的用户内容段类型: {Type}", p.Type);
                return;
        }
    }

    private static ChatImageDetailLevel? MapOpenAiImageDetail(string? s) =>
        s?.ToLowerInvariant() switch
        {
            "low" => ChatImageDetailLevel.Low,
            "high" => ChatImageDetailLevel.High,
            "auto" => ChatImageDetailLevel.Auto,
            _ => null
        };



    private AssistantChatMessage BuildAssistantMessage(CoreChatMessage msg)
    {
        if (msg.ToolCalls?.Count > 0)
        {
            var toolCalls = msg.ToolCalls.Select(tc =>
                ChatToolCall.CreateFunctionToolCall(
                    tc.Id,
                    tc.Function?.Name ?? "",
                    BinaryData.FromString(tc.Function?.Arguments ?? "{}")));
            return new AssistantChatMessage(toolCalls.ToList());
        }
        return new AssistantChatMessage(msg.Content);
    }

    private ChatCompletionOptions BuildOptions(ChatRequest request)
    {
        var options = new ChatCompletionOptions();

        if (request.Temperature.HasValue)
            options.Temperature = (float)request.Temperature.Value;

        if (request.TopP.HasValue)
            options.TopP = (float)request.TopP.Value;

        if (request.MaxTokens.HasValue)
            options.MaxOutputTokenCount = request.MaxTokens.Value;

        // 工具定义
        if (request.Tools?.Count > 0)
        {
            foreach (var tool in request.Tools)
            {
                if (tool.Function != null)
                {
                    options.Tools.Add(ChatTool.CreateFunctionTool(
                        tool.Function.Name,
                        tool.Function.Description,
                        BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(tool.Function.Parameters ?? new { }))));
                }
            }
        }

        return options;
    }

    private ChatResponse MapResponse(ChatCompletion completion, string model)
    {
        var message = new CoreChatMessage
        {
            Role = ChatRole.Assistant,
            Content = completion.Content.FirstOrDefault()?.Text ?? ""
        };

        // 处理工具调用
        if (completion.ToolCalls.Count > 0)
        {
            message.ToolCalls = completion.ToolCalls
                .Where(tc => tc.Kind == ChatToolCallKind.Function)
                .Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = tc.FunctionName,
                        Arguments = tc.FunctionArguments.ToString()
                    }
                }).ToList();
        }

        return new ChatResponse
        {
            Id = completion.Id,
            Model = completion.Model,
            Message = message,
            FinishReason = completion.FinishReason.ToString(),
            Usage = new TokenUsage
            {
                InputTokens = completion.Usage.InputTokenCount,
                OutputTokens = completion.Usage.OutputTokenCount
            }
        };
    }

    #endregion

    /// <summary>
    /// 聚合流式 tool_calls 增量（OpenAI SDK 2.x 无公开 Builder 时的等价实现）。
    /// </summary>
    private sealed class StreamingToolCallAccumulator
    {
        private readonly Dictionary<int, Slot> _byIndex = new();

        private sealed class Slot
        {
            public string Id = "";
            public string Name = "";
            public readonly StringBuilder Arguments = new();
        }

        public void Append(StreamingChatToolCallUpdate u)
        {
            var idx = u.Index;
            if (!_byIndex.TryGetValue(idx, out var slot))
            {
                slot = new Slot();
                _byIndex[idx] = slot;
            }

            if (!string.IsNullOrEmpty(u.ToolCallId))
                slot.Id = u.ToolCallId;
            if (!string.IsNullOrEmpty(u.FunctionName))
                slot.Name = u.FunctionName;
            if (u.FunctionArgumentsUpdate != null && !u.FunctionArgumentsUpdate.ToMemory().IsEmpty)
                slot.Arguments.Append(u.FunctionArgumentsUpdate.ToString());
        }

        public List<ToolCall> BuildFunctionToolCalls()
        {
            var list = new List<ToolCall>(_byIndex.Count);
            foreach (var kv in _byIndex.OrderBy(x => x.Key))
            {
                var s = kv.Value;
                if (string.IsNullOrEmpty(s.Name))
                    continue;
                list.Add(new ToolCall
                {
                    Id = s.Id,
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = s.Name,
                        Arguments = s.Arguments.Length > 0 ? s.Arguments.ToString() : "{}"
                    }
                });
            }

            return list;
        }
    }
}
