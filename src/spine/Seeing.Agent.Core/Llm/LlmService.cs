using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM 服务接口 - chat 专用门面。
/// <para>横切职责（Hook 生命周期、出站头合并、耗时统计、载荷组装）全部在
/// <see cref="ILlmInvoker"/>；本层只保留 chat 独有的目录归一化：
/// <c>MaxTokens</c> / <c>ThinkingEffort</c> / <c>Temperature</c>（spec §7.5）。</para>
/// <para>非 chat 调用方直接调 <see cref="ILlmInvoker"/>（配 <see cref="ILlmRouter"/>），不要绕经本门面。</para>
/// </summary>
public interface ILlmService : IModelConfigLookup
{
    /// <summary>
    /// 获取所有可用的文本模型配置（按模型 ID 索引）。
    /// </summary>
    IReadOnlyDictionary<string, ModelConfig> GetAvailableModels();
    /// <summary>
    /// 获取指定模型所属 Provider 的客户端，未找到返回 null。
    /// </summary>
    ILlmCapabilityClient? GetClientForModel(string modelId, LlmCapabilityKind capability);
    /// <summary>
    /// 获取指定 Provider 在指定能力上的客户端，未找到返回 null。
    /// </summary>
    ILlmCapabilityClient? GetClient(string providerId, LlmCapabilityKind capability);
    /// <summary>
    /// 非流式完成聊天补全，触发完整 Hook 链。
    /// </summary>
    Task<ChatResponse> CompleteAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// 非流式完成聊天补全，关联会话 ID 以触发 Hook 链。
    /// </summary>
    Task<ChatResponse> CompleteAsync(string modelId, ChatRequest request, string? sessionId, CancellationToken cancellationToken = default);
    /// <summary>
    /// 旁路非流式补全：不触发 Hook，直接透传请求。
    /// </summary>
    Task<ChatResponse> CompleteRawAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// 旁路流式补全：不触发 Hook，逐块透传增量。
    /// </summary>
    IAsyncEnumerable<StreamUpdate> CompleteRawStreamAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// 流式完成聊天补全，触发完整 Hook 链。
    /// </summary>
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(string modelId, ChatRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// 流式完成聊天补全，关联会话 ID 以触发 Hook 链。
    /// </summary>
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(string modelId, ChatRequest request, string? sessionId, CancellationToken cancellationToken = default);
    /// <summary>
    /// 测试指定 Provider 与模型在指定能力上的连通性。
    /// </summary>
    Task<bool> TestConnectionAsync(
        string providerId,
        string modelId,
        LlmCapabilityKind capability = LlmCapabilityKind.Chat,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// LLM 服务实现 - chat 门面：做目录归一化后把调用交给 <see cref="ILlmInvoker"/>。
/// <para>重试由客户端装饰器负责（工厂内部），因此 <c>llm.on_error</c> 只在重试耗尽后由横切层触发一次。</para>
/// </summary>
public class LlmService : ILlmService
{
    private readonly IProviderManager _providerManager;
    private readonly IModelManager _modelManager;
    private readonly ILlmInvoker _invoker;
    private readonly ILogger _logger;

    /// <summary>
    /// 注入 Provider/模型管理器与横切调用层构造 chat 门面。
    /// </summary>
    public LlmService(
        IProviderManager providerManager,
        IModelManager modelManager,
        ILlmInvoker invoker,
        ILogger<LlmService> logger)
    {
        _providerManager = providerManager;
        _modelManager = modelManager;
        _invoker = invoker;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有文本类型模型配置。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetAvailableModels()
        => _modelManager.GetModelsByType(ModelType.Text);

    /// <summary>
    /// 按模型 ID 从目录解析模型配置，未找到返回 null。
    /// </summary>
    public ModelConfig? GetModelConfig(string modelId)
        => _modelManager.GetModel(modelId);

    /// <summary>
    /// 获取指定模型所属 Provider 的客户端。
    /// </summary>
    public ILlmCapabilityClient? GetClientForModel(string modelId, LlmCapabilityKind capability)
        => _providerManager.GetClientForModel(modelId, capability);

    /// <summary>
    /// 获取指定 Provider 在指定能力上的客户端。
    /// </summary>
    public ILlmCapabilityClient? GetClient(string providerId, LlmCapabilityKind capability)
        => _providerManager.GetClient(providerId, capability);

    /// <summary>
    /// 非流式补全（无会话上下文的便捷重载）。
    /// </summary>
    public async Task<ChatResponse> CompleteAsync(
        string modelId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
        => await CompleteAsync(modelId, request, sessionId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 旁路非流式补全：仅做请求预处理，不触发任何 Hook。
    /// </summary>
    public async Task<ChatResponse> CompleteRawAsync(
        string modelId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var modelConfig = PrepareRequest(modelId, request);
        _logger.LogDebug("发送旁路聊天请求(无 Hook): Model={Model}", modelConfig.Id);

        return await _invoker.InvokeAsync<IChatCompletionClient, ChatResponse>(
            modelId,
            (client, call, token) => client.CompleteAsync(request, call, token),
            new LlmInvocationOptions { EmitHooks = false },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 旁路流式补全：仅做请求预处理，不触发任何 Hook，逐块透传增量。
    /// </summary>
    public async IAsyncEnumerable<StreamUpdate> CompleteRawStreamAsync(
        string modelId,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelConfig = PrepareRequest(modelId, request);
        _logger.LogDebug("发送旁路流式请求(无 Hook): Model={Model}", modelConfig.Id);

        await foreach (var update in _invoker
            .StreamAsync<IChatCompletionClient, StreamUpdate>(
                modelId,
                (client, call, token) => client.CompleteStreamAsync(request, call, token),
                new LlmInvocationOptions { EmitHooks = false },
                cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// chat 独有的目录归一化：补 MaxTokens、按目录夹紧思考档位、把请求模型换成 API ID。
    /// <para>客户端由 <see cref="ILlmInvoker"/> 经五层校验后供出，本层不再自己找客户端。</para>
    /// </summary>
    private ModelConfig PrepareRequest(string modelId, ChatRequest request)
    {
        var modelConfig = _modelManager.GetModel(modelId)
            ?? throw new InvalidOperationException($"未找到模型配置: {modelId}");

        if (request.MaxTokens == null && modelConfig.Limit?.Output > 0)
        {
            request.MaxTokens = modelConfig.Limit.Output;
            _logger.LogDebug("应用模型输出限制: Model={Model}, MaxTokens={MaxTokens}", modelId, modelConfig.Limit.Output);
        }

        ApplyThinkingCatalog(modelConfig, request, _logger);

        request.Model = string.IsNullOrEmpty(modelConfig.Id) ? modelId : modelConfig.Id;
        return modelConfig;
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

        // 不再在这里回落到 thinking.Default：默认档是「会话绑定模型时的初值」
        // （见 ModelManager.ApplyModelToSession）。请求期再回落会让会话里明确选的
        // 「不指定」被模型默认档悄悄顶替，用户看到「不指定」却仍然发出思考字段。
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
            // 也不回落到 thinking.Default：Default 只在会话首次绑定模型时作为初值写入会话
            // （ModelManager.ApplyModelToSession）。请求期再回落等于把模型默认档当兜底，
            // 会让会话里明确设的档位被静默改写。
            logger?.LogWarning(
                "思考强度 key 不在模型 levels 内，将按未指定处理: Model={Model}, Key={Key}",
                modelConfig.Id,
                candidate);

            request.ThinkingEffort = null;
            request.ThinkingBudgetTokens = null;
            request.EchoReasoningContent = false;
            return;
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

    /// <summary>
    /// 把 <c>llm.params</c> 可变参数袋里钩子改写过的值落到 chat 请求上。
    /// <para>顺序与改造前一致：钩子值 &gt; 请求值 &gt; 框架缺省（temperature 0.7 / topP 1.0 / maxTokens 目录上限）。
    /// 思考档位落在请求上之后再过一次目录夹紧——钩子给了个模型不支持的 key 时回落缺省档，
    /// 而不是把非法 key 直接发出站。</para>
    /// </summary>
    private void ApplyParamsHookOutput(ChatRequest request, LlmCallContext call, ModelConfig modelConfig)
    {
        if (call.Items.TryGetValue(LlmInvoker.ParamsItemKey, out var raw) &&
            raw is IDictionary<string, object?> bag)
        {
            if (TryConvert<double>(bag, "temperature", out var temperature))
                request.Temperature = temperature;

            if (TryConvert<double>(bag, "topP", out var topP))
                request.TopP = topP;

            if (TryConvert<int>(bag, "maxTokens", out var maxTokens))
                request.MaxTokens = maxTokens;

            if (TryConvert<string>(bag, "thinkingEffort", out var effort) && !string.IsNullOrWhiteSpace(effort))
                request.ThinkingEffort = effort;
        }

        // 与改造前逐字对齐的缺省回落：钩子与请求都没给时仍按框架缺省出站
        request.Temperature ??= 0.7;
        request.TopP ??= 1.0;
        request.MaxTokens ??= ModelLimitDefaults.Output;

        ApplyThinkingCatalog(modelConfig, request, _logger);
    }

    private bool TryConvert<T>(IDictionary<string, object?> bag, string key, out T value)
    {
        value = default!;
        if (!bag.TryGetValue(key, out var raw) || raw is null)
            return false;

        try
        {
            if (raw is T typed)
            {
                value = typed;
                return true;
            }

            var target = typeof(T);
            if (target == typeof(double))
            {
                value = (T)(object)Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            if (target == typeof(int))
            {
                value = (T)(object)Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            if (target == typeof(string))
            {
                value = (T)(object)(raw.ToString() ?? string.Empty);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // 钩子写了放不进请求的类型：忽略该键、保留请求原值，但必须留痕（禁止静默吞异常）
            _logger.LogWarning(
                ex,
                "llm.params 的 {Key} 无法落到 chat 请求，已忽略该覆盖",
                key);
        }

        return false;
    }

    /// <summary>
    /// 非流式补全：交给横切层跑「解析 → 前置 Hook → 调用 → 终态 Hook」，本层只在闭包里落 chat 参数。
    /// </summary>
    public async Task<ChatResponse> CompleteAsync(
        string modelId,
        ChatRequest request,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var modelConfig = PrepareRequest(modelId, request);

        return await _invoker.InvokeAsync<IChatCompletionClient, ChatResponse>(
            modelId,
            (client, call, token) =>
            {
                ApplyParamsHookOutput(request, call, modelConfig);
                _logger.LogDebug("发送聊天请求: Model={Model}, Provider={Provider}", request.Model, client.ProviderId);
                return client.CompleteAsync(request, call, token);
            },
            new LlmInvocationOptions { SessionId = sessionId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式补全（无会话上下文的便捷重载）。
    /// </summary>
    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CompleteStreamAsync(modelId, request, sessionId: null, cancellationToken))
            yield return update;
    }

    /// <summary>
    /// 流式补全：横切层负责 Hook 与累计，本层负责把失败包装成 chat 专用异常类型。
    /// </summary>
    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId,
        ChatRequest request,
        string? sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelConfig = PrepareRequest(modelId, request);
        LlmCallContext? call = null;
        string? providerId = null;

        // 迭代器里 yield 不能与 catch 同块：沿用通道泵把流的异常与转发解耦
        var channel = Channel.CreateUnbounded<StreamUpdate>();
        var writer = channel.Writer;
        Exception? capturedException = null;
        var retryCount = 0;

        var processTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in _invoker
                    .StreamAsync<IChatCompletionClient, StreamUpdate>(
                        modelId,
                        (client, ctx, token) =>
                        {
                            call = ctx;
                            providerId = client.ProviderId;
                            ApplyParamsHookOutput(request, ctx, modelConfig);
                            _logger.LogDebug(
                                "发送流式聊天请求: Model={Model}, Provider={Provider}",
                                request.Model,
                                client.ProviderId);
                            return client.CompleteStreamAsync(request, ctx, token);
                        },
                        new LlmInvocationOptions { SessionId = sessionId },
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                }

                writer.Complete();
            }
            catch (Exception ex)
            {
                capturedException = ex;
                if (call?.Items.TryGetValue(LlmRetryPolicy.AttemptItemKey, out var attemptObj) == true &&
                    attemptObj is int attempt && attempt > 1)
                {
                    retryCount = attempt - 1;
                }

                _logger.LogError(ex, "流式聊天请求失败: Model={Model}", request.Model);
                writer.Complete(ex);
            }
        }, cancellationToken);

        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
            yield return update;

        await processTask.ConfigureAwait(false);

        if (capturedException is null)
            yield break;

        var resolvedProviderId = providerId ?? string.Empty;
        throw capturedException switch
        {
            OperationCanceledException oce when !oce.CancellationToken.IsCancellationRequested
                => new LlmTimeoutException(timeout: TimeSpan.FromMinutes(5), oce)
                { ModelId = request.Model, ProviderId = resolvedProviderId, RetryCount = retryCount },

            IOException ioEx
                => new LlmStreamingException("流式响应读取失败", ioEx)
                { ModelId = request.Model, ProviderId = resolvedProviderId, RetryCount = retryCount },

            // 兜底分支必须重算 IsRetryable，不能硬编码 false：
            // 传输层重试耗尽后原样抛出的 HttpRequestException（5xx / 429）会走到这里，
            // 若标成不可重试，应用层轮次重试（DefaultLlmTurnRetryPolicy.CanRetry）直接返回 false，
            // 「传输层耗尽后由应用层接管」的链路在此断掉。
            _ => new LlmException($"LLM 请求失败: {capturedException.Message}", capturedException)
            {
                ModelId = request.Model,
                ProviderId = resolvedProviderId,
                IsRetryable = LlmRetryPolicy.IsRetryable(capturedException, CancellationToken.None),
                RetryCount = retryCount
            }
        };
    }

    /// <summary>
    /// 测试指定 Provider 与模型的连通性，委托 ProviderManager 实现。
    /// </summary>
    public async Task<bool> TestConnectionAsync(
        string providerId,
        string modelId,
        LlmCapabilityKind capability = LlmCapabilityKind.Chat,
        CancellationToken cancellationToken = default)
        => await _providerManager
            .TestConnectionAsync(providerId, modelId, capability, cancellationToken)
            .ConfigureAwait(false);
}
