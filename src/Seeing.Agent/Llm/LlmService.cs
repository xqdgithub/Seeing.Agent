using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Seeing.Agent.Llm;

/// <summary>
/// LLM 服务接口 - 统一管理模型配置和客户端调用
/// </summary>
public interface ILlmService
{
    /// <summary>获取所有可用模型</summary>
    IReadOnlyDictionary<string, ModelConfig> GetAvailableModels();

    /// <summary>获取指定模型配置</summary>
    ModelConfig? GetModelConfig(string modelId);

    /// <summary>获取指定模型的客户端</summary>
    ILlmClient? GetClientForModel(string modelId);

    /// <summary>获取指定 Provider 的客户端</summary>
    ILlmClient? GetClient(string providerId);

    /// <summary>发送聊天请求</summary>
    Task<ChatResponse> CompleteAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>发送聊天请求（带 Hook 支持）</summary>
    Task<ChatResponse> CompleteAsync(string modelId, ChatRequest request, string? sessionId, CancellationToken cancellationToken = default);

    /// <summary>发送流式聊天请求</summary>
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>发送流式聊天请求（带 Hook 支持）</summary>
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(string modelId, ChatRequest request, string? sessionId, CancellationToken cancellationToken = default);

    /// <summary>测试 Provider 连接</summary>
    Task<bool> TestConnectionAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// LLM 服务实现 - 统一管理模型配置和客户端调用
/// </summary>
public class LlmService : ILlmService
{
    private readonly SeeingAgentOptions _options;
    private readonly ILlmClientFactory _clientFactory;
    private readonly Core.Hooks.IHookManager _hookManager;
    private readonly ILogger _logger;

    // 模型配置缓存
    private readonly ConcurrentDictionary<string, ModelConfig> _modelConfigs;

    // 客户端缓存
    private readonly ConcurrentDictionary<string, ILlmClient> _clients;

    /// <summary>
    /// 创建 LLM 服务
    /// </summary>
    public LlmService(
        IOptions<SeeingAgentOptions> options,
        ILlmClientFactory clientFactory,
        Core.Hooks.IHookManager hookManager,
        ILogger<LlmService> logger)
    {
        _options = options.Value;
        _clientFactory = clientFactory;
        _hookManager = hookManager;
        _logger = logger;

        _modelConfigs = new ConcurrentDictionary<string, ModelConfig>();
        _clients = new ConcurrentDictionary<string, ILlmClient>();

        // 预定义 + SeeingAgent.Models / ModelScope + 各 Provider.models
        InitializeModels();

        // 初始化客户端
        InitializeClients();
    }

    /// <summary>获取所有可用模型</summary>
    public IReadOnlyDictionary<string, ModelConfig> GetAvailableModels()
    {
        return _modelConfigs;
    }

    /// <summary>获取指定模型配置</summary>
    public ModelConfig? GetModelConfig(string modelId)
    {
        // 直接匹配
        if (_modelConfigs.TryGetValue(modelId, out var config))
            return config;

        // 带 provider 前缀匹配 (如 openai/gpt-4o)
        foreach (var provider in _options.Providers.Keys)
        {
            var prefixedId = $"{provider}/{modelId}";
            if (_modelConfigs.TryGetValue(prefixedId, out config))
                return config;
        }

        return null;
    }

    /// <summary>获取指定模型的客户端</summary>
    public ILlmClient? GetClientForModel(string modelId)
    {
        var modelConfig = GetModelConfig(modelId);
        if (modelConfig == null)
        {
            _logger.LogWarning("未找到模型配置: {ModelId}", modelId);
            return null;
        }

        return GetClient(modelConfig.Provider);
    }

    /// <summary>获取指定 Provider 的客户端</summary>
    public ILlmClient? GetClient(string providerId)
    {
        return _clients.TryGetValue(providerId, out var client) ? client : null;
    }

    /// <summary>发送聊天请求</summary>
    public async Task<ChatResponse> CompleteAsync(
        string modelId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        return await CompleteAsync(modelId, request, sessionId: null, cancellationToken);
    }

    /// <summary>发送聊天请求（带 Hook 支持）</summary>
    public async Task<ChatResponse> CompleteAsync(
        string modelId,
        ChatRequest request,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var modelConfig = GetModelConfig(modelId);
        if (modelConfig == null)
            throw new InvalidOperationException($"未找到模型配置: {modelId}");

        // 应用模型输出限制
        if (request.MaxTokens == null && modelConfig.Limit?.Output > 0)
        {
            request.MaxTokens = modelConfig.Limit.Output;
            _logger.LogDebug("应用模型输出限制: Model={Model}, MaxTokens={MaxTokens}", modelId, modelConfig.Limit.Output);
        }

        var client = GetClient(modelConfig.Provider);
        if (client == null)
            throw new InvalidOperationException($"未找到模型 {modelId} 的客户端");

        var apiModelId = string.IsNullOrEmpty(modelConfig.Id) ? modelId : modelConfig.Id;
        request.Model = apiModelId;

        // ========== Hook: chat.before_start ==========
        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatBeforeStart,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = apiModelId,
                ["provider"] = client.ProviderId
            },
            cancellationToken: cancellationToken);

        // ========== Hook: chat.params ==========
        var paramsOutput = new Dictionary<string, object?>
        {
            ["temperature"] = request.Temperature ?? 0.7,
            ["topP"] = request.TopP ?? 1.0,
            ["topK"] = 0,
            ["maxTokens"] = request.MaxTokens ?? 4096
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
            cancellationToken);

        // 应用 Hook 修改后的参数
        request.Temperature = Convert.ToDouble(paramsOutput["temperature"]);
        request.TopP = Convert.ToDouble(paramsOutput["topP"]);
        request.MaxTokens = Convert.ToInt32(paramsOutput["maxTokens"]);

        // ========== Hook: chat.headers ==========
        var headersOutput = new Dictionary<string, object?>
        {
            ["headers"] = new Dictionary<string, string>()
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
            cancellationToken);

        // ========== Hook: llm.system_prompt ==========
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
                    ["modelId"] = modelId
                },
                promptOutput,
                cancellationToken);

            request.SystemPrompt = promptOutput["prompt"]?.ToString();
        }

        _logger.LogDebug("发送聊天请求: Model={Model}, Provider={Provider}", apiModelId, client.ProviderId);

        ChatResponse response;
        try
        {
            response = await client.CompleteAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // ========== Hook: chat.on_error ==========
            _hookManager.TriggerFireAndForget(
                HookRegistry.ChatOnError,
                sessionId ?? string.Empty,
                new Dictionary<string, object?>
                {
                    ["modelId"] = modelId,
                    ["provider"] = client.ProviderId,
                    ["error"] = ex
                });
            throw;
        }

        // ========== Hook: chat.message ==========
        await _hookManager.TriggerParallelAsync(
            HookRegistry.ChatMessage,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["messageId"] = response.Id,
                ["modelId"] = modelId
            },
            cancellationToken);

        // ========== Hook: chat.after_complete ==========
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

    /// <summary>发送流式聊天请求</summary>
    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CompleteStreamAsync(modelId, request, sessionId: null, cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>发送流式聊天请求（带 Hook 支持）</summary>
    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId,
        ChatRequest request,
        string? sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelConfig = GetModelConfig(modelId);
        if (modelConfig == null)
            throw new InvalidOperationException($"未找到模型配置: {modelId}");

        // 应用模型输出限制
        if (request.MaxTokens == null && modelConfig.Limit?.Output > 0)
        {
            request.MaxTokens = modelConfig.Limit.Output;
            _logger.LogDebug("应用模型输出限制: Model={Model}, MaxTokens={MaxTokens}", modelId, modelConfig.Limit.Output);
        }

        var client = GetClient(modelConfig.Provider);
        if (client == null)
            throw new InvalidOperationException($"未找到模型 {modelId} 的客户端");

        var apiModelId = string.IsNullOrEmpty(modelConfig.Id) ? modelId : modelConfig.Id;
        request.Model = apiModelId;

        // ========== Hook: chat.before_start ==========
        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ChatBeforeStart,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["provider"] = client.ProviderId,
                ["streaming"] = true
            },
            cancellationToken: cancellationToken);

        // ========== Hook: chat.params ==========
        var paramsOutput = new Dictionary<string, object?>
        {
            ["temperature"] = request.Temperature ?? 0.7,
            ["topP"] = request.TopP ?? 1.0,
            ["maxTokens"] = request.MaxTokens ?? 4096
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
            cancellationToken);

        request.Temperature = Convert.ToDouble(paramsOutput["temperature"]);
        request.TopP = Convert.ToDouble(paramsOutput["topP"]);
        request.MaxTokens = Convert.ToInt32(paramsOutput["maxTokens"]);

        // ========== Hook: llm.system_prompt ==========
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
                cancellationToken);

            request.SystemPrompt = promptOutput["prompt"]?.ToString();
        }

        _logger.LogDebug("发送流式聊天请求: Model={Model}, Provider={Provider}", apiModelId, client.ProviderId);

        var startTime = DateTime.Now;
        var maxRetries = _options.Providers.TryGetValue(modelConfig.Provider, out var providerConfig)
            ? providerConfig.MaxRetries
            : 3;
        var retryDelay = TimeSpan.FromSeconds(1);

        // 流式数据累计变量
        var streamedContent = new StringBuilder();
        var streamedReasoning = new StringBuilder();
        var streamedToolCalls = new List<ToolCall>();
        TokenUsage? streamedUsage = null;

        // 使用 Channel 解决 C# 不允许 yield 在 try-catch 中的限制
        // 这样可以确保异常能够正确传播给用户，不会静默吞掉
        var channel = Channel.CreateUnbounded<StreamUpdate>();
        var writer = channel.Writer;
        var messageId = Guid.NewGuid().ToString("N");
        Exception? capturedException = null;
        var retryCount = 0;

        // 在后台处理流式数据，捕获任何异常并支持重试
        var processTask = Task.Run(async () =>
        {
            var attempt = 0;
            while (attempt < maxRetries)
            {
                attempt++;
                try
                {
                    // 如果是重试，需要重置状态
                    if (attempt > 1)
                    {
                        messageId = Guid.NewGuid().ToString("N");
                        streamedContent.Clear();
                        streamedReasoning.Clear();
                        streamedToolCalls.Clear();
                        streamedUsage = null;

                        _logger.LogWarning(
                            "[LlmService] 流式请求重试: Model={Model}, Attempt={Attempt}/{MaxRetries}",
                            apiModelId, attempt, maxRetries);
                    }

                    await foreach (var update in client.CompleteStreamAsync(request, cancellationToken))
                    {
                        if (!string.IsNullOrEmpty(update.Id))
                            messageId = update.Id;
                        await writer.WriteAsync(update, cancellationToken);
                    }

                    // 成功完成
                    writer.Complete();
                    retryCount = attempt - 1;
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries && IsRetryableException(ex, cancellationToken))
                {
                    // 可重试的瞬态故障
                    capturedException = ex;
                    _logger.LogWarning(ex,
                        "[LlmService] 流式请求失败，准备重试: Model={Model}, Attempt={Attempt}/{MaxRetries}, Error={Error}",
                        apiModelId, attempt, maxRetries, ex.Message);

                    // 触发 chat.on_error Hook（可重试场景）
                    _hookManager.TriggerFireAndForget(
                        HookRegistry.ChatOnError,
                        sessionId ?? string.Empty,
                        new Dictionary<string, object?>
                        {
                            ["modelId"] = modelId,
                            ["provider"] = client.ProviderId,
                            ["error"] = ex,
                            ["attempt"] = attempt,
                            ["maxRetries"] = maxRetries,
                            ["willRetry"] = true
                        });

                    // 指数退避
                    var delay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex)
                {
                    // 不可重试或已达到最大重试次数
                    capturedException = ex;
                    _logger.LogError(ex, "流式聊天请求失败: Model={Model}, Attempt={Attempt}", apiModelId, attempt);

                    // 触发 chat.on_error Hook（最终失败）
                    _hookManager.TriggerFireAndForget(
                        HookRegistry.ChatOnError,
                        sessionId ?? string.Empty,
                        new Dictionary<string, object?>
                        {
                            ["modelId"] = modelId,
                            ["provider"] = client.ProviderId,
                            ["error"] = ex,
                            ["attempt"] = attempt,
                            ["maxRetries"] = maxRetries,
                            ["willRetry"] = false
                        });

                    writer.Complete(ex);
                    return;
                }
            }
        }, cancellationToken);

        // 从 channel 读取并 yield 返回给调用者，同时累计数据
        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
        {
            // 累计内容
            if (!string.IsNullOrEmpty(update.ContentDelta))
                streamedContent.Append(update.ContentDelta);

            // 累计推理内容
            if (!string.IsNullOrEmpty(update.ReasoningDelta))
                streamedReasoning.Append(update.ReasoningDelta);

            // 累计工具调用
            if (update.ToolCallDeltas != null && update.ToolCallDeltas.Count > 0)
            {
                foreach (var toolCall in update.ToolCallDeltas)
                {
                    var existingCall = streamedToolCalls.FirstOrDefault(tc => tc.Id == toolCall.Id);
                    if (existingCall != null)
                    {
                        // 追加到现有工具调用
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

            // 累计使用统计（通常在最后一个 update 中）
            if (update.Usage != null)
                streamedUsage = update.Usage;

            yield return update;
        }

        // 确保后台任务完成
        await processTask;

        // ✅ 如果有异常，包装并抛出
        if (capturedException != null)
        {
            // 根据异常类型包装
            var wrappedException = capturedException switch
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

            throw wrappedException;
        }

        // ========== Hook: chat.after_complete ==========
        var completeResult = new Dictionary<string, object?>
        {
            ["content"] = streamedContent.ToString(),
            ["reasoning"] = streamedReasoning.ToString(),
            ["usage"] = streamedUsage,
            ["toolCalls"] = streamedToolCalls,
            ["duration"] = DateTime.Now - startTime
        };

        _hookManager.TriggerFireAndForget(
            HookRegistry.ChatAfterComplete,
            sessionId ?? "",
            input: new Dictionary<string, object?> { ["modelId"] = modelId, ["streaming"] = true },
            result: completeResult);
    }

    /// <summary>测试 Provider 连接</summary>
    public async Task<bool> TestConnectionAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var client = GetClient(providerId);
        if (client == null)
        {
            _logger.LogWarning("未找到 Provider: {ProviderId}", providerId);
            return false;
        }

        return await client.TestConnectionAsync(cancellationToken);
    }

    #region 私有方法

    /// <summary>
    /// 合并预定义、SeeingAgent.Models、ModelScope.models、各 Provider.models
    /// </summary>
    private void InitializeModels()
    {
        //foreach (var (key, config) in PredefinedModels.GetAll())
        //    RegisterModelEntry(key, config);

        if (_options.ModelScope?.Models != null)
        {
            foreach (var (modelId, config) in _options.ModelScope.Models)
            {
                EnsureModelDefaults(modelId, config, defaultProvider: null);
                ApplyDefaultProviderIfMissing(modelId, config);
                RegisterModelEntry(modelId, config);
            }
        }

        if (_options.Models != null)
        {
            foreach (var (modelId, config) in _options.Models)
            {
                EnsureModelDefaults(modelId, config, defaultProvider: null);
                ApplyDefaultProviderIfMissing(modelId, config);
                RegisterModelEntry(modelId, config);
            }
        }

        foreach (var (providerId, providerConfig) in _options.Providers)
        {
            if (providerConfig.Models == null)
                continue;

            foreach (var (modelId, config) in providerConfig.Models)
            {
                EnsureModelDefaults(modelId, config, providerId);
                RegisterModelEntry($"{providerId}/{modelId}", config);
                //if (!_modelConfigs.ContainsKey(modelId))
                //    RegisterModelEntry(modelId, config);
            }
        }

        _logger.LogDebug("已加载 {Count} 个模型配置", _modelConfigs.Count);
    }

    private static void EnsureModelDefaults(string modelId, ModelConfig config, string? defaultProvider)
    {
        if (string.IsNullOrEmpty(config.Id))
            config.Id = modelId;
        if (string.IsNullOrEmpty(config.Provider) && defaultProvider != null)
            config.Provider = defaultProvider;
    }

    /// <summary>
    /// ModelScope / 顶层 Models 未写 provider 时回退到 SeeingAgent:DefaultProvider
    /// </summary>
    private void ApplyDefaultProviderIfMissing(string modelId, ModelConfig config)
    {
        if (!string.IsNullOrEmpty(config.Provider))
            return;
        if (!string.IsNullOrEmpty(_options.DefaultProvider))
        {
            config.Provider = _options.DefaultProvider;
            return;
        }

        _logger.LogWarning(
            "模型目录项 {ModelKey}（id={ModelId}）未配置 provider，且 DefaultProvider 为空，调用时将无法解析客户端",
            modelId,
            config.Id);
    }

    private void RegisterModelEntry(string key, ModelConfig config)
    {
        _modelConfigs[key] = config;
    }

    /// <summary>
    /// 初始化客户端
    /// </summary>
    private void InitializeClients()
    {
        foreach (var (providerId, providerConfig) in _options.Providers)
        {
            if (string.IsNullOrWhiteSpace(providerConfig.Id))
                providerConfig.Id = providerId;
            else if (!string.Equals(providerConfig.Id, providerId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Providers 字典键为 {Key}，但配置 id 为 {Id}。客户端注册在键 {Key} 下，请保证 ModelConfig.Provider 与该键一致。",
                    providerId,
                    providerConfig.Id,
                    providerId);
            }

            if (!_clientFactory.SupportsType(providerConfig.Type))
            {
                _logger.LogWarning("不支持的 Provider 类型: {ProviderId} ({Type})", providerId, providerConfig.Type);
                continue;
            }

            try
            {
                var client = _clientFactory.Create(providerConfig);
                _clients[providerId] = client;
                _logger.LogInformation("已初始化 Provider 客户端: {ProviderId} ({Type})", providerId, providerConfig.Type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化 Provider 客户端失败: {ProviderId}", providerId);
            }
        }
    }

    #endregion

    /// <summary>
    /// 判断异常是否为可重试的瞬态故障
    /// </summary>
    private static bool IsRetryableException(Exception ex, CancellationToken cancellationToken)
    {
        return ex is TimeoutException
            || ex is HttpRequestException
            || ex is IOException
            // OperationCanceledException 仅当非用户主动取消时才重试
            || ex is OperationCanceledException oce && oce.CancellationToken != cancellationToken && !cancellationToken.IsCancellationRequested;
    }
}