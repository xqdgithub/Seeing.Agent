using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// 经统一 LLM 基础设施（<see cref="ILlmInvoker"/> → <see cref="ILlmRouter"/> →
/// <see cref="IEmbeddingClient"/>）的向量服务，取代原先的裸 HTTP 旁路。
/// <para>重试装饰器、出站头合并、代理、Hook、用量统计从此对 embedding 一致生效
/// （spec §7.8）。旧的 <c>ProviderEmbeddingService</c> 已删除。</para>
/// <para><b>解析发生在调用期，不在 DI 期</b>：本类始终被注册，未启用时抛明确异常，
/// 由 <see cref="IEmbeddingStatus"/>（<see cref="ConfigurableEmbeddingStatus"/>）决定是否建向量索引。</para>
/// <para><b>模型只取用户显式配置的 <c>Embedding.ModelId</c></b>：不读
/// <c>DefaultModels.embedding</c>、不查目录挑第一个（决议 10 / §13 原则 1）。</para>
/// </summary>
public sealed class RouterEmbeddingService : IEmbeddingService
{
    private readonly ILlmInvoker _invoker;
    private readonly IModelConfigManager _models;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ILogger<RouterEmbeddingService>? _logger;
    private int _dimensions;

    public RouterEmbeddingService(
        ILlmInvoker invoker,
        IModelConfigManager models,
        IOptionsMonitor<MemoryOptions> options,
        ILogger<RouterEmbeddingService>? logger = null)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>维度在首次成功调用后回写；未启用时保持 0（不再有配置里的 <c>Dimensions</c> 声明）。</remarks>
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public string ProviderName
    {
        get
        {
            var modelRef = _options.CurrentValue.Embedding.ModelId;
            return string.IsNullOrWhiteSpace(modelRef) ? "unconfigured" : ProviderPart(modelRef);
        }
    }

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
    {
        var batch = await EmbedBatchAsync(new[] { text }, ct).ConfigureAwait(false);
        return batch[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var modelRef = RequireConfiguredModelRef();
        var wireModelId = EmbeddingModelReference.ToWireModelId(_models, modelRef);

        _logger?.LogDebug("向量调用: ModelRef={ModelRef}, ApiModel={ApiModel}, Count={Count}",
            modelRef, wireModelId, texts.Count);

        EmbeddingResponse response;
        try
        {
            response = await _invoker.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                modelRef,
                (client, call, token) => client.EmbedAsync(
                    new EmbeddingRequest
                    {
                        Model = wireModelId,
                        Inputs = texts.ToArray(),
                    },
                    call,
                    token),
                options: new LlmInvocationOptions(),
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (LlmCapabilityMismatchException ex)
        {
            // 解析失败不降级、不猜模型：原样上抛（异常已含 modelId + 五层校验原因），
            // 这里只补一条记忆侧上下文日志，便于把“哪个配置项坏了”定位到 memory.json。
            _logger?.LogError(ex,
                "记忆向量模型解析失败: ModelRef={ModelRef}, Reason={Reason}",
                ex.ModelId, ex.Reason);
            throw;
        }

        var tokens = response.Usage?.TotalTokens ?? 0;
        _dimensions = response.Dimensions;

        var results = new List<EmbeddingResult>(response.Vectors.Count);
        foreach (var vector in response.Vectors)
        {
            // Index 是服务端回显的输入序号：越界时回落到位次，避免整批失败。
            var text = vector.Index >= 0 && vector.Index < texts.Count ? texts[vector.Index] : vector.Text;
            results.Add(new EmbeddingResult(text, vector.Values, tokens));
        }

        if (results.Count == 0)
            throw new InvalidOperationException($"向量模型 '{modelRef}' 返回空响应，未得到任何向量。");

        return results;
    }

    /// <summary>
    /// 取当前显式配置的模型引用；未启用即抛错（不回落、不挑模型）。
    /// </summary>
    private string RequireConfiguredModelRef()
    {
        var embedding = _options.CurrentValue.Embedding;

        if (!embedding.Enabled)
            throw new InvalidOperationException(
                "记忆向量检索未启用：memory.json 需同时设置 Embedding.Enabled=true 与 Embedding.ModelId。");

        if (string.IsNullOrWhiteSpace(embedding.ModelId))
            throw new InvalidOperationException(
                "记忆向量检索未启用：Embedding.ModelId 为空。框架不会自动挑选模型，请显式配置形如 provider/model 的引用。");

        return embedding.ModelId;
    }

    private static string ProviderPart(string modelRef)
    {
        var slash = modelRef.LastIndexOf('/');
        return slash > 0 ? modelRef[..slash] : modelRef;
    }
}
