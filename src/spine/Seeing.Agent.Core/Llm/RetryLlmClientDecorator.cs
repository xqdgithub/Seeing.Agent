using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 重试装饰器：把 Provider 配置里的重试参数（退避 / 预算 / 次数）套到客户端外层，
/// 覆盖全部八个能力端口（spec §7.7）。
/// <para>分派只能靠 <c>is</c> 判断——决议 15 删掉了客户端自述能力，
/// <see cref="ProviderConfig.Capabilities"/> 是唯一真相，客户端身上没有
/// <c>Capabilities</c> 可读。因此隐含契约是<b>每个客户端实例恰好实现一个能力端口</b>：
/// 零匹配或多匹配都抛 <see cref="NotSupportedException"/>，
/// 绝不静默返回未包装的 inner（那会让该能力静默失去重试）。</para>
/// <para>重试边界与原 <c>RetryingLlmClient</c> 完全一致：非流式失败可重试；
/// 流式 / 轮询一旦向调用方交付过元素就绝不重开。</para>
/// </summary>
public sealed class RetryLlmClientDecorator : ILlmClientDecorator
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RetryLlmClientDecorator> _logger;

    /// <summary>
    /// 注入日志工厂构造重试装饰器。
    /// </summary>
    public RetryLlmClientDecorator(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<RetryLlmClientDecorator>();
    }

    /// <summary>
    /// 装饰器排序权重（100，值越小越靠内层）。
    /// </summary>
    public int Order => 100;

    /// <summary>
    /// 按 inner 实现的能力端口分派到对应的 <c>Retrying&lt;X&gt;Client</c>。
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// inner 未实现任何已知能力端口，或同时实现了多个端口（无法判定按哪个端口重试）。
    /// </exception>
    public ILlmCapabilityClient Wrap(ILlmCapabilityClient inner, ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);

        // 幂等保护：装饰器链被重复 Apply 时不得套出「重试的重试」——那会让退避与预算翻倍。
        if (inner is LlmRetryingClientBase)
        {
            _logger.LogDebug(
                "[RetryLlmClient] 客户端已带重试装饰，跳过重复包裹: Provider={Provider}",
                inner.ProviderId);
            return inner;
        }

        var capability = ResolveSingleCapability(inner);
        var settings = LlmRetrySettings.From(config);

        return capability switch
        {
            LlmCapabilityKind.Chat => new RetryingChatCompletionClient(
                (IChatCompletionClient)inner, settings, _loggerFactory.CreateLogger<RetryingChatCompletionClient>()),
            LlmCapabilityKind.Embedding => new RetryingEmbeddingClient(
                (IEmbeddingClient)inner, settings, _loggerFactory.CreateLogger<RetryingEmbeddingClient>()),
            LlmCapabilityKind.Rerank => new RetryingRerankClient(
                (IRerankClient)inner, settings, _loggerFactory.CreateLogger<RetryingRerankClient>()),
            LlmCapabilityKind.Decision => new RetryingDecisionClient(
                (IDecisionClient)inner, settings, _loggerFactory.CreateLogger<RetryingDecisionClient>()),
            LlmCapabilityKind.SpeechSynthesis => new RetryingSpeechSynthesisClient(
                (ISpeechSynthesisClient)inner, settings, _loggerFactory.CreateLogger<RetryingSpeechSynthesisClient>()),
            LlmCapabilityKind.SpeechTranscription => new RetryingSpeechTranscriptionClient(
                (ISpeechTranscriptionClient)inner, settings, _loggerFactory.CreateLogger<RetryingSpeechTranscriptionClient>()),
            LlmCapabilityKind.ImageGeneration => new RetryingImageGenerationClient(
                (IImageGenerationClient)inner, settings, _loggerFactory.CreateLogger<RetryingImageGenerationClient>()),
            LlmCapabilityKind.VideoGeneration => new RetryingVideoGenerationClient(
                (IVideoGenerationClient)inner, settings, _loggerFactory.CreateLogger<RetryingVideoGenerationClient>()),

            // 端口在 LlmCapabilityMap 登记了、这里却没有对应包装：明确报错，不静默降级为「无重试」。
            _ => throw new NotSupportedException(
                $"能力 '{LlmCapabilityMap.ToConfigKey(capability)}' 尚无重试包装类，" +
                $"请在 {nameof(RetryLlmClientDecorator)}.{nameof(Wrap)} 中补齐（客户端类型 {inner.GetType().Name}）。")
        };
    }

    /// <summary>
    /// 用 <see cref="LlmCapabilityMap.ClientTypes"/> 反查 inner 实现的能力端口，
    /// 并强制「恰好一个」的隐含契约（spec §7.2 / 决议 15）。
    /// </summary>
    private static LlmCapabilityKind ResolveSingleCapability(ILlmCapabilityClient inner)
    {
        var matched = new List<LlmCapabilityKind>();
        foreach (var (kind, portType) in LlmCapabilityMap.ClientTypes)
        {
            if (portType.IsInstanceOfType(inner))
                matched.Add(kind);
        }

        if (matched.Count == 0)
            throw new NotSupportedException(
                $"客户端 {inner.GetType().Name} 未实现任何已知能力端口" +
                $"（{string.Join(", ", LlmCapabilityMap.ClientTypes.Keys.Select(LlmCapabilityMap.ToConfigKey))}），" +
                "重试装饰器无法确定包装形态。约定：每个客户端实例恰好实现一个能力端口。");

        if (matched.Count > 1)
            throw new NotSupportedException(
                $"客户端 {inner.GetType().Name} 同时实现了 {matched.Count} 个能力端口" +
                $"（{string.Join(", ", matched.Select(LlmCapabilityMap.ToConfigKey))}）；" +
                "客户端不自述能力（决议 15），装饰器无法判定按哪个端口重试，请拆分为每端口一个实例。");

        return matched[0];
    }
}

/// <summary>
/// 单次装饰所用的重试参数，全部来自 <see cref="ProviderConfig"/>（缺省值与迁移前一致）。
/// </summary>
internal readonly record struct LlmRetrySettings(
    TimeSpan BaseDelay,
    TimeSpan MaxDelay,
    TimeSpan Budget,
    int MaxRetries)
{
    public static LlmRetrySettings From(ProviderConfig config) => new(
        BaseDelay: TimeSpan.FromMilliseconds(config.RetryBaseDelayMs > 0 ? config.RetryBaseDelayMs : 500),
        MaxDelay: TimeSpan.FromMilliseconds(config.RetryMaxDelayMs > 0 ? config.RetryMaxDelayMs : 10_000),
        Budget: TimeSpan.FromMilliseconds(config.RetryTotalBudgetMs > 0 ? config.RetryTotalBudgetMs : 120_000),
        MaxRetries: config.MaxRetries);
}

/// <summary>
/// 重试包装类的公共骨架：身份与连通性转发、退避计算、<see cref="LlmCallContext.Items"/> 记账、
/// 内层释放转发、可选 <see cref="IModelCatalogClient"/> 透传。
/// <para>重试边界只在本类实现一次，八个能力端口共享，避免逐端口抄写时走样：</para>
/// <list type="bullet">
/// <item><description>非流式：瞬时失败按指数退避重试，受次数上限与累计预算约束。</description></item>
/// <item><description>流式 / 轮询：仅在尚未向调用方交付任何元素时重开；交付过就绝不重试（无法回滚已交付内容），
/// 已产出后的重开由应用层（AgentExecutor + <see cref="ILlmTurnRetryPolicy"/>）负责。</description></item>
/// <item><description>不可重放的请求体（如已部分消费且不可定位的音频流）：放弃重试，抛原始异常。</description></item>
/// </list>
/// </summary>
public abstract class LlmRetryingClientBase : ILlmCapabilityClient, IModelCatalogClient, IDisposable, IAsyncDisposable
{
    private readonly ILlmCapabilityClient _inner;
    private readonly LlmRetrySettings _settings;
    private readonly ILogger _logger;

    internal LlmRetryingClientBase(ILlmCapabilityClient inner, LlmRetrySettings settings, ILogger logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _settings = settings;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderId => _inner.ProviderId;

    /// <inheritdoc />
    public string ProviderType => _inner.ProviderType;

    /// <summary>
    /// 连通性测试直接转发内层，<b>不重试</b>：连接测试的语义是「一次真实探针的结果」，
    /// 重试会把瞬时故障掩盖成可用（spec §7.13 / 决议 25）。
    /// </summary>
    public Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken cancellationToken = default)
        => _inner.TestConnectionAsync(modelId, capability, call, cancellationToken);

    /// <summary>
    /// 可选目录端口的透传。装饰后 <c>ConfiguredLlmProvider</c> 只能看到包装对象，
    /// 若不透传，<see cref="ProviderConfig.ListModels"/> 的远端目录合并会被装饰层静默抹掉。
    /// 内层未实现该端口时返回空目录——与「没有远端目录」的既有语义一致，不抛。
    /// </summary>
    Task<IReadOnlyList<ModelConfig>> IModelCatalogClient.ListModelsAsync(CancellationToken cancellationToken) =>
        _inner is IModelCatalogClient catalog
            ? catalog.ListModelsAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<ModelConfig>>(Array.Empty<ModelConfig>());

    /// <summary>
    /// 向内层转发释放：Provider 持有的是装饰后的对象，不转发会导致内层客户端
    /// （自建 HttpClient / handler 连接池）泄漏。
    /// </summary>
    public void Dispose()
    {
        if (_inner is IDisposable disposable)
            disposable.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>异步释放：优先转发内层的 <see cref="IAsyncDisposable"/>，否则回落同步 <see cref="Dispose"/>。</summary>
    public async ValueTask DisposeAsync()
    {
        switch (_inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 非流式重试：瞬时失败按退避重试，直到成功、异常不可重试、或次数 / 预算耗尽（抛原始异常）。
    /// </summary>
    /// <param name="operation">日志与诊断用的操作名。</param>
    /// <param name="invoke">发起一次调用的委托（每次重试都重新调用，不在失败时复用半途状态）。</param>
    /// <param name="call">调用期上下文；用于写回重试记账键，可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="prepareRetry">
    /// 重试前的「请求体可重放」准备：返回 <c>false</c> 表示无法重放，直接放弃重试。
    /// 用于 <see cref="TranscriptionRequest.Audio"/> 这类被读走的流（决议 17：流归调用方所有，装饰器不得 Dispose）。
    /// </param>
    protected async Task<TResult> RunWithRetryAsync<TResult>(
        string operation,
        Func<Task<TResult>> invoke,
        LlmCallContext? call,
        CancellationToken cancellationToken,
        Func<bool>? prepareRetry = null)
    {
        var attempt = 0;
        var elapsed = TimeSpan.Zero;
        while (true)
        {
            attempt++;
            WriteRetryItems(call, attempt, willRetry: false, nextDelay: null);
            try
            {
                return await invoke().ConfigureAwait(false);
            }
            catch (Exception ex) when (LlmRetryPolicy.IsRetryable(ex, cancellationToken))
            {
                var nextDelay = LlmRetryPolicy.ComputeDelay(attempt, _settings.BaseDelay, _settings.MaxDelay);
                if (!LlmRetryPolicy.ShouldRetry(
                        attempt, elapsed, nextDelay, _settings.MaxRetries, _settings.Budget))
                {
                    throw;
                }

                if (prepareRetry is not null && !prepareRetry())
                {
                    _logger.LogWarning(ex,
                        "[RetryLlmClient] 请求体不可重放，放弃重试: Provider={Provider}, Operation={Operation}, Attempt={Attempt}",
                        ProviderId, operation, attempt);
                    throw;
                }

                elapsed += nextDelay;
                WriteRetryItems(call, attempt, willRetry: true, nextDelay);
                _logger.LogWarning(ex,
                    "[RetryLlmClient] 非流式重试: Provider={Provider}, Operation={Operation}, Attempt={Attempt}/{Max}, NextDelayMs={NextDelayMs}, ElapsedMs={ElapsedMs}, BudgetMs={BudgetMs}",
                    ProviderId, operation, attempt, FormatMax(_settings.MaxRetries),
                    nextDelay.TotalMilliseconds, elapsed.TotalMilliseconds, _settings.Budget.TotalMilliseconds);
                await Task.Delay(nextDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 无返回值的非流式重试便捷重载（例如作业的取消调用）。
    /// </summary>
    protected Task RunWithRetryAsync(
        string operation,
        Func<Task> invoke,
        LlmCallContext? call,
        CancellationToken cancellationToken,
        Func<bool>? prepareRetry = null)
        => RunWithRetryAsync(
            operation,
            async () =>
            {
                await invoke().ConfigureAwait(false);
                return 0;
            },
            call,
            cancellationToken,
            prepareRetry);

    /// <summary>
    /// 流式 / 轮询重试：把「一次枚举」交给 <see cref="TakeOnceAsync{TItem}"/> 观察，
    /// 只有在<b>尚未产出任何元素</b>且异常可重试时才退避重开；其余情形把原始异常抛给调用方。
    /// </summary>
    protected async IAsyncEnumerable<TItem> RunWithRetryStreamAsync<TItem>(
        string operation,
        Func<IAsyncEnumerable<TItem>> open,
        LlmCallContext? call,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Func<bool>? prepareRetry = null)
    {
        var attempt = 0;
        var elapsed = TimeSpan.Zero;
        while (true)
        {
            attempt++;
            WriteRetryItems(call, attempt, willRetry: false, nextDelay: null);
            var yielded = false;
            Exception? captured = null;

            await foreach (var item in TakeOnceAsync(
                open(), () => yielded = true, ex => captured = ex, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            if (captured is null)
                yield break;

            // 传输层边界：一旦向调用方交付过 chunk，绝不在此重试（无法回滚已交付内容）。
            // 已产出后的重开由应用层（AgentExecutor + ILlmTurnRetryPolicy）负责。
            if (yielded || !LlmRetryPolicy.IsRetryable(captured, cancellationToken))
                throw captured;

            var nextDelay = LlmRetryPolicy.ComputeDelay(attempt, _settings.BaseDelay, _settings.MaxDelay);
            if (!LlmRetryPolicy.ShouldRetry(
                    attempt, elapsed, nextDelay, _settings.MaxRetries, _settings.Budget))
            {
                WriteRetryItems(call, attempt, willRetry: false, nextDelay: null);
                throw captured;
            }

            if (prepareRetry is not null && !prepareRetry())
            {
                _logger.LogWarning(captured,
                    "[RetryLlmClient] 请求体不可重放，放弃流式重试: Provider={Provider}, Operation={Operation}, Attempt={Attempt}",
                    ProviderId, operation, attempt);
                WriteRetryItems(call, attempt, willRetry: false, nextDelay: null);
                throw captured;
            }

            elapsed += nextDelay;
            WriteRetryItems(call, attempt, willRetry: true, nextDelay);
            _logger.LogWarning(captured,
                "[RetryLlmClient] 流式重试: Provider={Provider}, Operation={Operation}, Attempt={Attempt}/{Max}, NextDelayMs={NextDelayMs}, ElapsedMs={ElapsedMs}, BudgetMs={BudgetMs}",
                ProviderId, operation, attempt, FormatMax(_settings.MaxRetries),
                nextDelay.TotalMilliseconds, elapsed.TotalMilliseconds, _settings.Budget.TotalMilliseconds);
            await Task.Delay(nextDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 枚举一次内层流，用回调把「已产出元素」「失败异常」上报外层，据此决定能否重开。
    /// 手动驱动枚举器是为了捕获 <c>MoveNextAsync</c> 抛出的异常——它在迭代器里无法被外层 try 捕获。
    /// </summary>
    private static async IAsyncEnumerable<TItem> TakeOnceAsync<TItem>(
        IAsyncEnumerable<TItem> source,
        Action onYield,
        Action<Exception> onError,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    onError(ex);
                    yield break;
                }

                if (!moved)
                    yield break;

                onYield();
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>把重试状态写进 <see cref="LlmCallContext.Items"/>，供 Hook 载荷与诊断读取（与原实现同键）。</summary>
    private void WriteRetryItems(LlmCallContext? call, int attempt, bool willRetry, TimeSpan? nextDelay)
    {
        if (call is null)
            return;

        call.Items[LlmRetryPolicy.AttemptItemKey] = attempt;
        call.Items[LlmRetryPolicy.WillRetryItemKey] = willRetry;
        call.Items[LlmRetryPolicy.MaxRetriesItemKey] = _settings.MaxRetries;
        if (nextDelay is { } delay)
            call.Items[LlmRetryPolicy.NextDelayItemKey] = delay.TotalMilliseconds;
    }

    private static string FormatMax(int maxRetries) => maxRetries > 0 ? maxRetries.ToString() : "∞";
}

/// <summary>
/// 带强类型内层引用的重试骨架：八个端口包装类各以自身端口参数化本类，
/// 从而在成员里直接调用端口方法而不必重复强转。
/// </summary>
/// <typeparam name="TInner">被包装的能力端口类型。</typeparam>
public abstract class LlmRetryingClientBase<TInner> : LlmRetryingClientBase
    where TInner : ILlmCapabilityClient
{
    private readonly TInner _inner;

    internal LlmRetryingClientBase(TInner inner, LlmRetrySettings settings, ILogger logger)
        : base(inner, settings, logger)
        => _inner = inner;

    /// <summary>强类型的内层端口引用。</summary>
    protected TInner Inner => _inner;
}

/// <summary>
/// <see cref="IChatCompletionClient"/> 的重试包装：非流式补全可重试；
/// 流式补全仅在尚未产出任何 chunk 时重开。
/// </summary>
public sealed class RetryingChatCompletionClient : LlmRetryingClientBase<IChatCompletionClient>, IChatCompletionClient
{
    internal RetryingChatCompletionClient(
        IChatCompletionClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<ChatResponse> CompleteAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(CompleteAsync), () => Inner.CompleteAsync(request, call, ct), call, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryStreamAsync(
            nameof(CompleteStreamAsync), () => Inner.CompleteStreamAsync(request, call, ct), call, ct);
}

/// <summary>
/// <see cref="IEmbeddingClient"/> 的重试包装。向量化是幂等批量读，瞬时故障可安全重试。
/// </summary>
public sealed class RetryingEmbeddingClient : LlmRetryingClientBase<IEmbeddingClient>, IEmbeddingClient
{
    internal RetryingEmbeddingClient(
        IEmbeddingClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(EmbedAsync), () => Inner.EmbedAsync(request, call, ct), call, ct);
}

/// <summary>
/// <see cref="IRerankClient"/> 的重试包装（本次只出契约，包装先行以免新增实现漏重试）。
/// </summary>
public sealed class RetryingRerankClient : LlmRetryingClientBase<IRerankClient>, IRerankClient
{
    internal RetryingRerankClient(
        IRerankClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<RerankResponse> RerankAsync(
        RerankRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(RerankAsync), () => Inner.RerankAsync(request, call, ct), call, ct);
}

/// <summary>
/// <see cref="IDecisionClient"/> 的重试包装：评估与目录列举都是可重放的读操作。
/// </summary>
public sealed class RetryingDecisionClient : LlmRetryingClientBase<IDecisionClient>, IDecisionClient
{
    internal RetryingDecisionClient(
        IDecisionClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<DecisionResponse> EvaluateAsync(
        DecisionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(EvaluateAsync), () => Inner.EvaluateAsync(request, call, ct), call, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<DecisionModel>> ListModelsAsync(CancellationToken ct = default)
        => RunWithRetryAsync(nameof(ListModelsAsync), () => Inner.ListModelsAsync(ct), call: null, ct);
}

/// <summary>
/// <see cref="ISpeechSynthesisClient"/> 的重试包装：非流式合成可重试；
/// 流式合成按音频块判定——交付过任何一块就不再重开（否则调用方会拿到两段拼接音频）。
/// </summary>
public sealed class RetryingSpeechSynthesisClient : LlmRetryingClientBase<ISpeechSynthesisClient>, ISpeechSynthesisClient
{
    internal RetryingSpeechSynthesisClient(
        ISpeechSynthesisClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(SynthesizeAsync), () => Inner.SynthesizeAsync(request, call, ct), call, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<AudioChunk> SynthesizeStreamAsync(
        SpeechSynthesisRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryStreamAsync(
            nameof(SynthesizeStreamAsync), () => Inner.SynthesizeStreamAsync(request, call, ct), call, ct);
}

/// <summary>
/// <see cref="ISpeechTranscriptionClient"/> 的重试包装。
/// <para>ASR 的请求体带一个由调用方所有的音频流（决议 17）：一旦内层读过字节，
/// 原样重放就会把「后半段音频」当整段上传。因此重试前先尝试把流定位回起始位置；
/// 不可定位的流视为不可重放，直接抛出原始异常。装饰器全程不 Dispose 调用方的流。</para>
/// </summary>
public sealed class RetryingSpeechTranscriptionClient : LlmRetryingClientBase<ISpeechTranscriptionClient>, ISpeechTranscriptionClient
{
    internal RetryingSpeechTranscriptionClient(
        ISpeechTranscriptionClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        var prepareRetry = CreateAudioReplayGate(request);
        return RunWithRetryAsync(
            nameof(TranscribeAsync), () => Inner.TranscribeAsync(request, call, ct), call, ct, prepareRetry);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TranscriptSegment> TranscribeStreamAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        var prepareRetry = CreateAudioReplayGate(request);
        return RunWithRetryStreamAsync(
            nameof(TranscribeStreamAsync), () => Inner.TranscribeStreamAsync(request, call, ct), call, ct, prepareRetry);
    }

    /// <summary>
    /// 「音频流能否重放」闸门：可定位则每次重试前回卷到调用方的起始位置，否则拒绝重试。
    /// 只移动 <see cref="Stream.Position"/>，不关闭、不 Dispose 调用方的流。
    /// </summary>
    private static Func<bool> CreateAudioReplayGate(TranscriptionRequest request)
    {
        var audio = request.Audio;
        if (audio is null || !audio.CanSeek)
            return () => false;

        var start = audio.Position;
        return () =>
        {
            audio.Position = start;
            return true;
        };
    }
}

/// <summary>
/// <see cref="IImageGenerationClient"/> 的重试包装（本次只出契约，包装先行）。
/// </summary>
public sealed class RetryingImageGenerationClient : LlmRetryingClientBase<IImageGenerationClient>, IImageGenerationClient
{
    internal RetryingImageGenerationClient(
        IImageGenerationClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(GenerateAsync), () => Inner.GenerateAsync(request, call, ct), call, ct);
}

/// <summary>
/// <see cref="IVideoGenerationClient"/> 的重试包装（本次只出契约，包装先行）。
/// <para>作业型协议：提交 / 查状态 / 取消都是可重放的短请求，按非流式重试；
/// 轮询流沿用同一条边界——已经向调用方报出过状态就不再重开（回滚不了已见的进度）。</para>
/// </summary>
public sealed class RetryingVideoGenerationClient : LlmRetryingClientBase<IVideoGenerationClient>, IVideoGenerationClient
{
    internal RetryingVideoGenerationClient(
        IVideoGenerationClient inner,
        LlmRetrySettings settings,
        ILogger logger)
        : base(inner, settings, logger)
    {
    }

    /// <inheritdoc />
    public Task<MediaJobHandle> SubmitAsync(
        VideoGenerationRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(SubmitAsync), () => Inner.SubmitAsync(request, call, ct), call, ct);

    /// <inheritdoc />
    public Task<MediaJobStatus> GetStatusAsync(
        MediaJobHandle handle,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(GetStatusAsync), () => Inner.GetStatusAsync(handle, call, ct), call, ct);

    /// <inheritdoc />
    public Task CancelAsync(
        MediaJobHandle handle,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryAsync(nameof(CancelAsync), () => Inner.CancelAsync(handle, call, ct), call, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<MediaJobStatus> PollAsync(
        MediaJobHandle handle,
        TimeSpan interval,
        LlmCallContext? call = null,
        CancellationToken ct = default)
        => RunWithRetryStreamAsync(
            nameof(PollAsync), () => Inner.PollAsync(handle, interval, call, ct), call, ct);
}
