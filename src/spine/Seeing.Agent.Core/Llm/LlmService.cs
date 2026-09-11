using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM 服务接口 - 统一管理模型配置和客户端调用
/// </summary>
public interface ILlmService : IModelConfigLookup
{
    IReadOnlyDictionary<string, ModelConfig> GetAvailableModels();
    ILlmClient? GetClientForModel(string modelId);
    ILlmClient? GetClient(string providerId);
    Task<ChatResponse> CompleteAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    Task<ChatResponse> CompleteAsync(string modelId, ChatRequest request, string? sessionId, CancellationToken cancellationToken = default);
    Task<ChatResponse> CompleteRawAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<StreamUpdate> CompleteRawStreamAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(string modelId, ChatRequest request, string? sessionId, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(string providerId, string modelId, CancellationToken cancellationToken = default);
}

/// <summary>
/// LLM 服务实现 - 调用编排层：构造 Call、跑 Hook；重试由客户端装饰器负责。
/// </summary>
public class LlmService : ILlmService
{
    private readonly IProviderManager _providerManager;
    private readonly IModelManager _modelManager;
    private readonly UnifiedConfigManager _configManager;
    private readonly IHookManager _hookManager;
    private readonly ILogger _logger;

    public LlmService(
        IProviderManager providerManager,
        IModelManager modelManager,
        UnifiedConfigManager configManager,
        IHookManager hookManager,
        ILogger<LlmService> logger)
    {
        _providerManager = providerManager;
        _modelManager = modelManager;
        _configManager = configManager;
        _hookManager = hookManager;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, ModelConfig> GetAvailableModels()
        => _modelManager.GetModelsByType(ModelType.Text);

    public ModelConfig? GetModelConfig(string modelId)
        => _modelManager.GetModel(modelId);

    public ILlmClient? GetClientForModel(string modelId)
        => _providerManager.GetClientForModel(modelId);

    public ILlmClient? GetClient(string providerId)
        => _providerManager.GetClient(providerId);

    public async Task<ChatResponse> CompleteAsync(
        string modelId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
        => await CompleteAsync(modelId, request, sessionId: null, cancellationToken).ConfigureAwait(false);

    public async Task<ChatResponse> CompleteRawAsync(
        string modelId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var (client, apiModelId, _) = PrepareClientRequest(modelId, request);
        _logger.LogDebug("发送旁路聊天请求(无 Hook): Model={Model}, Provider={Provider}", apiModelId, client.ProviderId);
        return await client.CompleteAsync(request, call: null, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteRawStreamAsync(
        string modelId,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (client, apiModelId, _) = PrepareClientRequest(modelId, request);
        _logger.LogDebug("发送旁路流式请求(无 Hook): Model={Model}, Provider={Provider}", apiModelId, client.ProviderId);

        await foreach (var update in client.CompleteStreamAsync(request, call: null, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private (ILlmClient Client, string ApiModelId, ModelConfig ModelConfig) PrepareClientRequest(string modelId, ChatRequest request)
    {
        var modelConfig = _modelManager.GetModel(modelId)
            ?? throw new InvalidOperationException($"未找到模型配置: {modelId}");

        if (request.MaxTokens == null && modelConfig.Limit?.Output > 0)
        {
            request.MaxTokens = modelConfig.Limit.Output;
            _logger.LogDebug("应用模型输出限制: Model={Model}, MaxTokens={MaxTokens}", modelId, modelConfig.Limit.Output);
        }

        ApplyThinkingCatalog(modelConfig, request, _logger);

        var client = _providerManager.GetClient(modelConfig.Provider)
            ?? throw new InvalidOperationException($"未找到模型 {modelId} 的客户端");

        var apiModelId = string.IsNullOrEmpty(modelConfig.Id) ? modelId : modelConfig.Id;
        request.Model = apiModelId;
        return (client, apiModelId, modelConfig);
    }

    /// <summary>
    /// 按模型目录规范化 ThinkingEffort，并填写 ThinkingBudgetTokens。不拼 vendor JSON 字段名。
    /// </summary>
    internal static void ApplyThinkingCatalog(ModelConfig modelConfig, ChatRequest request, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(modelConfig);
        ArgumentNullException.ThrowIfNull(request);

        var thinking = modelConfig.Options?.Thinking;
        if (!ThinkingEffortKeys.IsSupported(thinking))
        {
            request.ThinkingEffort = null;
            request.ThinkingBudgetTokens = null;
            request.EchoReasoningContent = false;
            return;
        }

        var levels = thinking!.Levels!;
        var candidate = string.IsNullOrWhiteSpace(request.ThinkingEffort)
            ? null
            : request.ThinkingEffort.Trim();

        if (string.IsNullOrEmpty(candidate) && !string.IsNullOrWhiteSpace(thinking.Default))
            candidate = thinking.Default.Trim();

        if (string.IsNullOrEmpty(candidate))
        {
            request.ThinkingEffort = null;
            request.ThinkingBudgetTokens = null;
            request.EchoReasoningContent = false;
            return;
        }

        var match = FindLevel(levels, candidate);
        if (match is null)
        {
            logger?.LogWarning(
                "思考强度 key 不在模型 levels 内，将回落: Model={Model}, Key={Key}, Default={Default}",
                modelConfig.Id,
                candidate,
                thinking.Default);

            if (!string.IsNullOrWhiteSpace(thinking.Default))
                match = FindLevel(levels, thinking.Default.Trim());

            if (match is null)
            {
                request.ThinkingEffort = null;
                request.ThinkingBudgetTokens = null;
                request.EchoReasoningContent = false;
                return;
            }
        }

        request.ThinkingEffort = match.Key;
        if (ThinkingEffortKeys.IsOff(match.Key))
        {
            request.ThinkingBudgetTokens = null;
            request.EchoReasoningContent = false;
            return;
        }

        request.ThinkingBudgetTokens = match.BudgetTokens ?? thinking.BudgetTokens;
        request.EchoReasoningContent = string.Equals(
            thinking.Interleaved,
            "reasoning_content",
            StringComparison.OrdinalIgnoreCase);
    }

    private static ThinkingLevel? FindLevel(IReadOnlyList<ThinkingLevel> levels, string key)
    {
        foreach (var level in levels)
        {
            if (string.Equals(level.Key, key, StringComparison.OrdinalIgnoreCase))
                return level;
        }

        return null;
    }

    private static void ApplyChatParamsToRequest(ChatRequest request, IDictionary<string, object?> paramsOutput, ModelConfig modelConfig, ILogger logger)
    {
        if (paramsOutput.TryGetValue("temperature", out var t) && t is not null)
            request.Temperature = Convert.ToDouble(t);
        if (paramsOutput.TryGetValue("topP", out var p) && p is not null)
            request.TopP = Convert.ToDouble(p);
        if (paramsOutput.TryGetValue("maxTokens", out var m) && m is not null)
            request.MaxTokens = Convert.ToInt32(m);

        if (paramsOutput.TryGetValue("thinkingEffort", out var effort) && effort is not null)
            request.ThinkingEffort = effort.ToString();

        ApplyThinkingCatalog(modelConfig, request, logger);
    }

    private static LlmCallContext CreateCallContext(string? sessionId)
        => new()
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            CorrelationId = Guid.NewGuid().ToString("N")
        };

    private static void MergeHeadersHookOutput(LlmCallContext call, IDictionary<string, object?> headersOutput)
    {
        if (!headersOutput.TryGetValue("headers", out var raw) || raw is null)
            return;

        IEnumerable<KeyValuePair<string, string>>? pairs = raw switch
        {
            IDictionary<string, string> dict => dict,
            IReadOnlyDictionary<string, string> readOnly => readOnly,
            _ => null
        };

        if (pairs is null)
            return;

        foreach (var (key, value) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(key))
                call.ExtraHeaders[key] = value;
        }
    }

    private void FireChatOnError(
        string modelId,
        string providerId,
        string? sessionId,
        LlmCallContext call,
        Exception ex)
    {
        var attempt = call.Items.TryGetValue(LlmRetryPolicy.AttemptItemKey, out var a) ? a : 1;
        var maxRetries = call.Items.TryGetValue(LlmRetryPolicy.MaxRetriesItemKey, out var m) ? m : 1;
        var willRetry = call.Items.TryGetValue(LlmRetryPolicy.WillRetryItemKey, out var w) && w is true;

        _hookManager.TriggerFireAndForget(
            HookRegistry.ChatOnError,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["provider"] = providerId,
                ["error"] = ex,
                ["attempt"] = attempt,
                ["maxRetries"] = maxRetries,
                ["willRetry"] = willRetry
            });
    }

    public async Task<ChatResponse> CompleteAsync(
        string modelId,
        ChatRequest request,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var (client, apiModelId, modelConfig) = PrepareClientRequest(modelId, request);
        var call = CreateCallContext(sessionId);

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatBeforeStart,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = apiModelId,
                ["provider"] = client.ProviderId
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var paramsOutput = new Dictionary<string, object?>
        {
            ["temperature"] = request.Temperature ?? 0.7,
            ["topP"] = request.TopP ?? 1.0,
            ["topK"] = 0,
            ["maxTokens"] = request.MaxTokens ?? 4096,
            ["thinkingEffort"] = request.ThinkingEffort
        };

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatParams,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["provider"] = client.ProviderId
            },
            paramsOutput,
            cancellationToken).ConfigureAwait(false);

        ApplyChatParamsToRequest(request, paramsOutput, modelConfig, _logger);
        var headersOutput = new Dictionary<string, object?>
        {
            ["headers"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatHeaders,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["provider"] = client.ProviderId
            },
            headersOutput,
            cancellationToken).ConfigureAwait(false);

        MergeHeadersHookOutput(call, headersOutput);

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            var promptOutput = new Dictionary<string, object?>
            {
                ["prompt"] = request.SystemPrompt
            };

            await _hookManager.TriggerBlockingAsync(
                HookRegistry.LlmSystemPrompt,
                sessionId ?? string.Empty,
                new Dictionary<string, object?> { ["modelId"] = modelId },
                promptOutput,
                cancellationToken).ConfigureAwait(false);

            request.SystemPrompt = promptOutput["prompt"]?.ToString();
        }

        _logger.LogDebug("发送聊天请求: Model={Model}, Provider={Provider}", apiModelId, client.ProviderId);

        ChatResponse response;
        try
        {
            response = await client.CompleteAsync(request, call, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FireChatOnError(modelId, client.ProviderId, sessionId, call, ex);
            throw;
        }

        await _hookManager.TriggerParallelAsync(
            HookRegistry.ChatMessage,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["messageId"] = response.Id,
                ["modelId"] = modelId
            },
            cancellationToken).ConfigureAwait(false);

        _hookManager.TriggerFireAndForget(
            HookRegistry.ChatAfterComplete,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["messageId"] = response.Id
            },
            new Dictionary<string, object?>
            {
                ["response"] = response
            });

        return response;
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CompleteStreamAsync(modelId, request, sessionId: null, cancellationToken))
            yield return update;
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId,
        ChatRequest request,
        string? sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (client, apiModelId, modelConfig) = PrepareClientRequest(modelId, request);
        var call = CreateCallContext(sessionId);

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatBeforeStart,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["provider"] = client.ProviderId,
                ["streaming"] = true
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var paramsOutput = new Dictionary<string, object?>
        {
            ["temperature"] = request.Temperature ?? 0.7,
            ["topP"] = request.TopP ?? 1.0,
            ["maxTokens"] = request.MaxTokens ?? 4096,
            ["thinkingEffort"] = request.ThinkingEffort
        };

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatParams,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["provider"] = client.ProviderId,
                ["streaming"] = true
            },
            paramsOutput,
            cancellationToken).ConfigureAwait(false);

        ApplyChatParamsToRequest(request, paramsOutput, modelConfig, _logger);

        var headersOutput = new Dictionary<string, object?>
        {
            ["headers"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatHeaders,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["provider"] = client.ProviderId,
                ["streaming"] = true
            },
            headersOutput,
            cancellationToken).ConfigureAwait(false);

        MergeHeadersHookOutput(call, headersOutput);

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            var promptOutput = new Dictionary<string, object?>
            {
                ["prompt"] = request.SystemPrompt
            };

            await _hookManager.TriggerBlockingAsync(
                HookRegistry.LlmSystemPrompt,
                sessionId ?? string.Empty,
                new Dictionary<string, object?>
                {
                    ["modelId"] = modelId,
                    ["streaming"] = true
                },
                promptOutput,
                cancellationToken).ConfigureAwait(false);

            request.SystemPrompt = promptOutput["prompt"]?.ToString();
        }

        _logger.LogDebug("发送流式聊天请求: Model={Model}, Provider={Provider}", apiModelId, client.ProviderId);
        var startTime = DateTime.Now;
        var streamedContent = new StringBuilder();
        var streamedReasoning = new StringBuilder();
        var streamedToolCalls = new List<ToolCall>();
        TokenUsage? streamedUsage = null;

        var channel = Channel.CreateUnbounded<StreamUpdate>();
        var writer = channel.Writer;
        Exception? capturedException = null;
        var retryCount = 0;

        var processTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in client.CompleteStreamAsync(request, call, cancellationToken))
                    await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);

                writer.Complete();
            }
            catch (Exception ex)
            {
                capturedException = ex;
                if (call.Items.TryGetValue(LlmRetryPolicy.AttemptItemKey, out var attemptObj) &&
                    attemptObj is int attempt && attempt > 1)
                    retryCount = attempt - 1;

                _logger.LogError(ex, "流式聊天请求失败: Model={Model}", apiModelId);
                FireChatOnError(modelId, client.ProviderId, sessionId, call, ex);
                writer.Complete(ex);
            }
        }, cancellationToken);

        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.ContentDelta))
                streamedContent.Append(update.ContentDelta);

            if (!string.IsNullOrEmpty(update.ReasoningDelta))
                streamedReasoning.Append(update.ReasoningDelta);

            if (update.ToolCallDeltas != null && update.ToolCallDeltas.Count > 0)
            {
                foreach (var toolCall in update.ToolCallDeltas)
                {
                    var existingCall = streamedToolCalls.FirstOrDefault(tc => tc.Id == toolCall.Id);
                    if (existingCall != null)
                    {
                        if (toolCall.Function != null)
                        {
                            existingCall.Function ??= new FunctionCall();
                            if (!string.IsNullOrEmpty(toolCall.Function.Name))
                                existingCall.Function.Name += toolCall.Function.Name;
                            if (!string.IsNullOrEmpty(toolCall.Function.Arguments))
                                existingCall.Function.Arguments += toolCall.Function.Arguments;
                        }
                    }
                    else
                    {
                        streamedToolCalls.Add(toolCall);
                    }
                }
            }

            if (update.Usage != null)
                streamedUsage = update.Usage;

            yield return update;
        }

        await processTask.ConfigureAwait(false);

        if (capturedException != null)
        {
            throw capturedException switch
            {
                OperationCanceledException oce when !oce.CancellationToken.IsCancellationRequested
                    => new LlmTimeoutException(timeout: TimeSpan.FromMinutes(5), oce)
                    { ModelId = apiModelId, ProviderId = client.ProviderId, RetryCount = retryCount },

                IOException ioEx
                    => new LlmStreamingException("流式响应读取失败", ioEx)
                    { ModelId = apiModelId, ProviderId = client.ProviderId, RetryCount = retryCount },

                _ => new LlmException($"LLM 请求失败: {capturedException.Message}", capturedException)
                { ModelId = apiModelId, ProviderId = client.ProviderId, IsRetryable = false, RetryCount = retryCount }
            };
        }

        _hookManager.TriggerFireAndForget(
            HookRegistry.ChatAfterComplete,
            sessionId ?? "",
            input: new Dictionary<string, object?> { ["modelId"] = modelId, ["streaming"] = true },
            result: new Dictionary<string, object?>
            {
                ["content"] = streamedContent.ToString(),
                ["reasoning"] = streamedReasoning.ToString(),
                ["usage"] = streamedUsage,
                ["toolCalls"] = streamedToolCalls,
                ["duration"] = DateTime.Now - startTime
            });
    }

    public async Task<bool> TestConnectionAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
        => await _providerManager.TestConnectionAsync(providerId, modelId, cancellationToken).ConfigureAwait(false);
}
