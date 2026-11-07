using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM 调用横切层实现：所有能力的统一调用管道（spec §7.5 的固定流程）。
/// <code>
/// Resolve&lt;TClient&gt;(modelId) → LlmCallContext → llm.params + llm.headers
///   → call()/stream() → llm.message → llm.after_complete（载荷经适配器组装）
///   → 异常时 llm.on_error 后原样抛出
/// </code>
/// <para><b>重试装饰器在客户端内部</b>（工厂应用），本层在其外层：重试发生在一次 Hook 周期之内，
/// <c>llm.on_error</c> 只在重试耗尽后触发一次。</para>
/// </summary>
public sealed class LlmInvoker : ILlmInvoker
{
    /// <summary>
    /// <c>llm.params</c> 可变参数袋在 <see cref="LlmCallContext.Items"/> 上的记账键。
    /// <para>Invoker 不认识各能力的请求 DTO，因此不直接把钩子值写进请求；它在调用前把
    /// （可能被钩子改写过的）参数袋放到这里，由调用方在 <c>call</c> 委托里读回并落到自己的请求对象。
    /// chat 侧的落地实现见 <see cref="LlmService"/>。</para>
    /// </summary>
    public const string ParamsItemKey = "llm.params";

    private readonly ILlmRouter _router;
    private readonly IHookManager _hooks;
    private readonly ILlmHookPayloadAdapterRegistry _adapters;
    private readonly ILogger<LlmInvoker> _logger;

    /// <summary>
    /// 注入路由器、Hook 管理与载荷适配器注册表构造横切层。
    /// </summary>
    public LlmInvoker(
        ILlmRouter router,
        IHookManager hooks,
        ILlmHookPayloadAdapterRegistry adapters,
        ILogger<LlmInvoker> logger)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<TResponse> InvokeAsync<TClient, TResponse>(
        string modelId,
        Func<TClient, LlmCallContext, CancellationToken, Task<TResponse>> call,
        LlmInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
        where TClient : class, ILlmCapabilityClient
    {
        ArgumentNullException.ThrowIfNull(call);

        var capability = CapabilityOf<TClient>();
        // 解析放在最前：五层校验不过就一个 Hook 都不触发，也不会产生任何出站请求
        var client = _router.Resolve<TClient>(modelId);
        var ctx = CreateCallContext(options);

        if (ShouldEmitHooks(options))
        {
            await EmitRequestHooksAsync(
                capability, modelId, client, ctx, streaming: false, cancellationToken).ConfigureAwait(false);
        }

        var started = Stopwatch.GetTimestamp();
        TResponse response;
        try
        {
            response = await call(client, ctx, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ShouldEmitHooks(options))
            {
                EmitOnError(capability, modelId, client, ctx, ex);
            }

            throw;
        }

        var duration = Stopwatch.GetElapsedTime(started);
        if (!ShouldEmitHooks(options))
            return response;

        var payload = BuildPayload(
            capability,
            modelId,
            client,
            response,
            usage: ExtractUsage(response),
            duration,
            options,
            streaming: false,
            messageId: ExtractMessageId(response),
            streamedContent: null,
            streamedReasoning: null,
            streamedToolCalls: null);

        // 顺序固定：llm.message（Parallel）在前，llm.after_complete（FireAndForget）在后
        await _hooks.TriggerParallelAsync(
            HookRegistry.LlmMessage,
            SessionIdOf(options),
            HookInput(capability, modelId, client, streaming: false, payload),
            cancellationToken).ConfigureAwait(false);

        _hooks.TriggerFireAndForget(
            HookRegistry.LlmAfterComplete,
            SessionIdOf(options),
            HookInput(capability, modelId, client, streaming: false, payload),
            _adapters.Resolve(capability).Build(payload));

        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TChunk> StreamAsync<TClient, TChunk>(
        string modelId,
        Func<TClient, LlmCallContext, CancellationToken, IAsyncEnumerable<TChunk>> stream,
        LlmInvocationOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TClient : class, ILlmCapabilityClient
    {
        ArgumentNullException.ThrowIfNull(stream);

        var capability = CapabilityOf<TClient>();
        var client = _router.Resolve<TClient>(modelId);
        var ctx = CreateCallContext(options);
        var emitHooks = ShouldEmitHooks(options);

        if (emitHooks)
        {
            await EmitRequestHooksAsync(
                capability, modelId, client, ctx, streaming: true, cancellationToken).ConfigureAwait(false);
        }

        var started = Stopwatch.GetTimestamp();
        var accumulator = new StreamAccumulator();

        // 累计与转发在同一次遍历里完成：契约只承诺 StreamUpdate 的累计语义（ILlmInvoker XML 注释）
        Exception? failure = null;
        var enumerator = stream(client, ctx, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                TChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;

                    chunk = enumerator.Current;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (chunk is StreamUpdate update)
                    accumulator.Apply(update);

                yield return chunk;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var duration = Stopwatch.GetElapsedTime(started);
        if (emitHooks)
        {
            if (failure is not null)
            {
                EmitOnError(capability, modelId, client, ctx, failure);
                throw failure;
            }

            var payload = BuildPayload(
                capability,
                modelId,
                client,
                response: null,
                accumulator.Usage,
                duration,
                options,
                streaming: true,
                messageId: accumulator.MessageId,
                streamedContent: accumulator.Content.ToString(),
                streamedReasoning: accumulator.Reasoning.ToString(),
                streamedToolCalls: accumulator.ToolCalls);

            _hooks.TriggerFireAndForget(
                HookRegistry.LlmAfterComplete,
                SessionIdOf(options),
                HookInput(capability, modelId, client, streaming: true, payload),
                _adapters.Resolve(capability).Build(payload));
        }
        else if (failure is not null)
        {
            throw failure;
        }
    }

    /// <summary>
    /// 出站前的两个阻塞 Hook：<c>llm.params</c>（可变参数袋）与 <c>llm.headers</c>（出站头）。
    /// </summary>
    private async Task EmitRequestHooksAsync(
        LlmCapabilityKind capability,
        string modelId,
        ILlmCapabilityClient client,
        LlmCallContext ctx,
        bool streaming,
        CancellationToken cancellationToken)
    {
        var input = new Dictionary<string, object?>
        {
            [HookDataContract.LlmAfterComplete.ModelId.Name] = modelId,
            [HookDataContract.LlmAfterComplete.Model.Name] = modelId,
            [HookDataContract.LlmAfterComplete.Provider.Name] = client.ProviderId,
            [HookDataContract.LlmAfterComplete.Capability.Name] = LlmCapabilityMap.ToHookValue(capability),
            [HookDataContract.LlmAfterComplete.Streaming.Name] = streaming
        };

        var parameters = SpecializedParameterKeys(capability);
        await _hooks.TriggerBlockingAsync(
            HookRegistry.LlmParams,
            ctx.SessionId ?? string.Empty,
            input,
            parameters,
            cancellationToken).ConfigureAwait(false);

        // 把（可能被钩子改写过的）参数袋交给调用方落地
        ctx.Items[ParamsItemKey] = parameters;

        var headers = new Dictionary<string, object?>
        {
            ["headers"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        await _hooks.TriggerBlockingAsync(
            HookRegistry.LlmHeaders,
            ctx.SessionId ?? string.Empty,
            input,
            headers,
            cancellationToken).ConfigureAwait(false);

        MergeHeaders(ctx, headers);
    }

    /// <summary>
    /// 终态错误 Hook：重试已在装饰器内耗尽，故一次调用只触发一次。
    /// </summary>
    private void EmitOnError(
        LlmCapabilityKind capability,
        string modelId,
        ILlmCapabilityClient client,
        LlmCallContext ctx,
        Exception ex)
    {
        _logger.LogError(
            ex,
            "LLM 调用失败: Model={ModelId}, Provider={ProviderId}, Capability={Capability}",
            modelId,
            client.ProviderId,
            LlmCapabilityMap.ToHookValue(capability));

        _hooks.TriggerFireAndForget(
            HookRegistry.LlmOnError,
            ctx.SessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.ModelId.Name] = modelId,
                [HookDataContract.LlmAfterComplete.Model.Name] = modelId,
                [HookDataContract.LlmAfterComplete.Provider.Name] = client.ProviderId,
                [HookDataContract.LlmAfterComplete.Capability.Name] = LlmCapabilityMap.ToHookValue(capability),
                ["error"] = ex,
                ["attempt"] = AttemptOf(ctx),
                ["maxRetries"] = ItemOf(ctx, LlmRetryPolicy.MaxRetriesItemKey) ?? 1,
                ["willRetry"] = ItemOf(ctx, LlmRetryPolicy.WillRetryItemKey) is true
            });
    }

    private LlmPayloadContext BuildPayload(
        LlmCapabilityKind capability,
        string modelId,
        ILlmCapabilityClient client,
        object? response,
        TokenUsage? usage,
        TimeSpan duration,
        LlmInvocationOptions? options,
        bool streaming,
        string? messageId,
        string? streamedContent,
        string? streamedReasoning,
        IReadOnlyList<ToolCall>? streamedToolCalls)
        => new()
        {
            Capability = capability,
            ProviderId = client.ProviderId,
            ModelId = modelId,
            Response = response,
            Usage = usage,
            Duration = duration,
            SessionId = options?.SessionId,
            Streaming = streaming,
            MessageId = messageId,
            // Step 属于执行轮次：横切层拿不到，调用方（执行器）需要时在 options 落地后再补
            Step = null,
            StreamedContent = streamedContent,
            StreamedReasoning = streamedReasoning,
            StreamedToolCalls = streamedToolCalls
        };

    /// <summary>after_complete / message 的 input 袋：身份与定位信息（result 由适配器产出）。</summary>
    private static Dictionary<string, object?> HookInput(
        LlmCapabilityKind capability,
        string modelId,
        ILlmCapabilityClient client,
        bool streaming,
        LlmPayloadContext payload)
        => new()
        {
            [HookDataContract.LlmAfterComplete.ModelId.Name] = modelId,
            [HookDataContract.LlmAfterComplete.Model.Name] = modelId,
            [HookDataContract.LlmAfterComplete.Provider.Name] = client.ProviderId,
            [HookDataContract.LlmAfterComplete.Capability.Name] = LlmCapabilityMap.ToHookValue(capability),
            [HookDataContract.LlmAfterComplete.Streaming.Name] = streaming,
            [HookDataContract.LlmAfterComplete.MessageId.Name] = payload.MessageId,
            [HookDataContract.LlmAfterComplete.Step.Name] = payload.Step
        };

    private static LlmCallContext CreateCallContext(LlmInvocationOptions? options)
        => new()
        {
            SessionId = string.IsNullOrWhiteSpace(options?.SessionId) ? null : options!.SessionId,
            ParentSessionId = string.IsNullOrWhiteSpace(options?.ParentSessionId) ? null : options!.ParentSessionId,
            CorrelationId = Guid.NewGuid().ToString("N")
        };

    private static bool ShouldEmitHooks(LlmInvocationOptions? options) => options?.EmitHooks ?? true;

    private static string SessionIdOf(LlmInvocationOptions? options)
        => options?.SessionId ?? string.Empty;

    private static object? ItemOf(LlmCallContext ctx, string key)
        => ctx.Items.TryGetValue(key, out var value) ? value : null;

    private static object AttemptOf(LlmCallContext ctx)
        => ItemOf(ctx, LlmRetryPolicy.AttemptItemKey) ?? 1;

    /// <summary>
    /// 端口类型 → 能力。判定规则统一在 <see cref="LlmCapabilityMap.TryResolveKind"/>，
    /// 本类只负责把「无法定位」翻译成可读异常。
    /// </summary>
    private static LlmCapabilityKind CapabilityOf<TClient>()
    {
        if (LlmCapabilityMap.TryResolveKind(typeof(TClient), out var capability))
            return capability;

        var matches = LlmCapabilityMap.FindKindsByClientType(typeof(TClient));

        throw new ArgumentException(
            $"请求端口 '{typeof(TClient).Name}' 无法定位能力（匹配 {matches.Count} 个端口）："
            + "请传 LlmCapabilityMap.ClientTypes 中登记的八个能力端口之一，且每个客户端恰好实现一个端口",
            nameof(TClient));
    }

    /// <summary>
    /// <c>llm.params</c> 的能力特化键（spec §7.6 / 决议 16）。
    /// <para>这里只给键名不给值：值属于各能力的请求 DTO，横切层不解释语义（spec §13 原则 2）。
    /// 钩子写入的值由调用方从 <see cref="ParamsItemKey"/> 读回并落到自己的请求。</para>
    /// </summary>
    private static Dictionary<string, object?> SpecializedParameterKeys(LlmCapabilityKind capability)
        => capability switch
        {
            LlmCapabilityKind.Chat => new Dictionary<string, object?>
            {
                ["temperature"] = null,
                ["topP"] = null,
                ["maxTokens"] = null,
                ["thinkingEffort"] = null
            },
            LlmCapabilityKind.Embedding => new Dictionary<string, object?>
            {
                ["dimensions"] = null,
                ["encodingFormat"] = null
            },
            LlmCapabilityKind.SpeechSynthesis => new Dictionary<string, object?>
            {
                ["voice"] = null,
                ["format"] = null,
                ["speed"] = null
            },
            LlmCapabilityKind.SpeechTranscription => new Dictionary<string, object?>
            {
                ["language"] = null
            },
            // Decision 无特化键（仅通用 modelId / provider / capability）；其余仅出契约的能力同理
            _ => new Dictionary<string, object?>()
        };

    private static void MergeHeaders(LlmCallContext ctx, IDictionary<string, object?> headersOutput)
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
                ctx.ExtraHeaders[key] = value;
        }
    }

    /// <summary>
    /// 从响应 DTO 上搬运 usage（纯字段读取，不是能力策略）；未知形状返回 null。
    /// </summary>
    private static TokenUsage? ExtractUsage(object? response) => response switch
    {
        ChatResponse chat => chat.Usage,
        EmbeddingResponse embedding => embedding.Usage,
        DecisionResponse decision => decision.Usage,
        SpeechSynthesisResult synthesis => synthesis.Usage,
        TranscriptionResult transcription => transcription.Usage,
        ImageGenerationResult image => image.Usage,
        _ => null
    };

    /// <summary>
    /// 从响应 DTO 上搬运消息 ID（仅 chat 有）；其余能力返回 null。
    /// </summary>
    private static string? ExtractMessageId(object? response)
        => response is ChatResponse chat && !string.IsNullOrWhiteSpace(chat.Id) ? chat.Id : null;

    /// <summary>
    /// 流式累计器：按 <see cref="ILlmInvoker.StreamAsync"/> 的契约累计 Id / 正文 / 推理 / 工具调用 / usage。
    /// </summary>
    private sealed class StreamAccumulator
    {
        private readonly List<ToolCall> _toolCalls = [];

        /// <summary>首个非空 chunk Id（即响应消息 ID）。</summary>
        public string? MessageId { get; private set; }

        /// <summary>累计正文。</summary>
        public StringBuilder Content { get; } = new();

        /// <summary>累计推理内容。</summary>
        public StringBuilder Reasoning { get; } = new();

        /// <summary>末次出现的 usage（供应商通常在尾块给出）。</summary>
        public TokenUsage? Usage { get; private set; }

        /// <summary>按 Id 合并后的工具调用集合。</summary>
        public IReadOnlyList<ToolCall> ToolCalls => _toolCalls;

        /// <summary>并入一个增量块。</summary>
        public void Apply(StreamUpdate update)
        {
            if (MessageId is null && !string.IsNullOrWhiteSpace(update.Id))
                MessageId = update.Id;

            if (!string.IsNullOrEmpty(update.ContentDelta))
                Content.Append(update.ContentDelta);

            if (!string.IsNullOrEmpty(update.ReasoningDelta))
                Reasoning.Append(update.ReasoningDelta);

            if (update.Usage is not null)
                Usage = update.Usage;

            if (update.ToolCallDeltas is not { Count: > 0 })
                return;

            foreach (var delta in update.ToolCallDeltas)
                Merge(delta);
        }

        private void Merge(ToolCall delta)
        {
            var existing = _toolCalls.FirstOrDefault(call => call.Id == delta.Id);
            if (existing is null)
            {
                _toolCalls.Add(delta);
                return;
            }

            if (delta.Function is null)
                return;

            existing.Function ??= new FunctionCall();
            if (!string.IsNullOrEmpty(delta.Function.Name))
                existing.Function.Name += delta.Function.Name;
            if (!string.IsNullOrEmpty(delta.Function.Arguments))
                existing.Function.Arguments += delta.Function.Arguments;
        }
    }
}
