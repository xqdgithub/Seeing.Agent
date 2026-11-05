using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.SystemOne.Clients;

/// <summary>
/// TypeSafe（Jev）Decision 协议客户端（spec §7.9）。
/// <para>由原 SystemOne 平行栈合流而来：现在只实现框架能力端口
/// <see cref="IDecisionClient"/>（评估 + 动态目录）与 <see cref="IModelCatalogClient"/>
/// （供 <c>ProviderConfig.ListModels = true</c> 时合并远端目录），不再有独立 provider 注册表。</para>
/// <para>刻意没有能力自述属性（决议 15）：能力真相在 <see cref="ProviderConfig.Capabilities"/>。</para>
/// </summary>
public sealed class TypeSafeSystemOneClient : IDecisionClient, IModelCatalogClient, IDisposable
{
    private readonly ProviderConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>构造 TypeSafe 客户端；HttpClient 由本类负责释放（工厂新建）。</summary>
    public TypeSafeSystemOneClient(
        ProviderConfig config,
        HttpClient http,
        ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderId => _config.Id;

    /// <inheritdoc />
    public string ProviderType => _config.Type;

    /// <inheritdoc />
    public async Task<DecisionResponse> EvaluateAsync(
        DecisionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = ResolveModel(request.Model);
        var wire = TypeSafeWireMap.FromRequest(request, model);

        _logger.LogDebug(
            "Decision 评估: provider={ProviderId}, model={Model}, questions={Count}",
            _config.Id, model, wire.Questions.Count);

        var response = await SystemOneHttpHelper.PostAsync<DecisionRequestWire, DecisionResponseWire>(
            _http, _config, TypeSafeProtocol.EvaluatePath, wire, call, ct).ConfigureAwait(false);

        return TypeSafeWireMap.ToResponse(response);
    }

    /// <summary>
    /// 动态模型目录：远端映射为 <see cref="ModelConfig"/>（Types 固定为 Decision）。
    /// <para>这是目录维度的唯一投影——<see cref="IDecisionClient"/> 不再重复声明
    /// 同义成员（那样会让同一 wire 端点被两个接口各投影一次）。</para>
    /// </summary>
    async Task<IReadOnlyList<ModelConfig>> IModelCatalogClient.ListModelsAsync(CancellationToken cancellationToken)
    {
        var models = TypeSafeWireMap.ToDecisionModels(
            await ListModelsCoreAsync(cancellationToken).ConfigureAwait(false));

        return models.Select(m => TypeSafeWireMap.ToModelConfig(m, _config.Id)).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken cancellationToken = default)
    {
        if (!capability.HasFlag(LlmCapabilityKind.Decision))
        {
            _logger.LogWarning(
                "TypeSafeSystemOneClient 只服务 Decision 能力，收到 {Capability} 连接测试请求", capability);
            return false;
        }

        try
        {
            // 决议 25：一次单 noul 问题的真实探针，校验返回概率字段。
            // 只探 /v1/models 会把「模型不存在」误判为连通。
            var response = await EvaluateAsync(
                new DecisionRequest
                {
                    State = "connection probe",
                    Model = modelId ?? string.Empty,
                    Questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
                    {
                        [TypeSafeProtocol.ProbeQuestionId] = new DecisionQuestion
                        {
                            Type = DecisionQuestionTypes.Noul,
                            Instructions = "Is the decision endpoint reachable?"
                        }
                    }
                },
                call,
                cancellationToken).ConfigureAwait(false);

            var ok = response.Answers.TryGetValue(TypeSafeProtocol.ProbeQuestionId, out var answer)
                     && answer?.Noul.HasValue == true;
            if (!ok)
                _logger.LogWarning("Decision 连接测试未返回 noul 概率字段: Model={Model}", modelId);

            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decision 连接测试失败: Model={Model}", modelId);
            return false;
        }
    }

    /// <summary>释放底层 HttpClient。</summary>
    public void Dispose() => _http.Dispose();

    private async Task<ModelsResponseWire> ListModelsCoreAsync(CancellationToken ct)
        => await SystemOneHttpHelper.GetAsync<ModelsResponseWire>(
            _http, _config, TypeSafeProtocol.ModelsPath, call: null, ct).ConfigureAwait(false);

    /// <summary>
    /// 解析出站模型 ID：剥掉 <c>provider/</c> 前缀后交给 wire。
    /// <para><b>为什么必须剥离</b>：目录键是 <c>ProviderId/ModelId</c>（如 <c>typesafe/jev-latest</c>），
    /// 而 TypeSafe 端点只认裸模型名（<c>jev-latest</c>）。参数字面量写着 <c>provider/model</c>
    /// 是目录约定，不是 wire 约定——embedding 侧有同样的剥离
    /// （<c>RouterEmbeddingService</c> → <c>EmbeddingModelReference.ToWireModelId</c>），此处保持同一口径。</para>
    /// <para>留空时用 Provider 自身配置的模型；未配或配了多个时<b>直接报错</b>，
    /// 不做「取首个」的启发式（spec §13 原则 1）。</para>
    /// </summary>
    private string ResolveModel(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return ToWireModelId(requested);

        var configured = _config.Models;
        if (configured is { Count: 1 })
            return ToWireModelId(configured.Keys.First());

        var detail = configured is { Count: > 1 }
            ? $"Provider '{_config.Id}' 配置了 {configured.Count} 个模型（{string.Join(", ", configured.Keys)}）"
            : $"Provider '{_config.Id}' 未配置任何模型";
        throw new InvalidOperationException(
            $"Decision 请求未指定 model，且{detail}；无法唯一确定模型。请显式传入 model，或设置 DefaultModels.decision。");
    }

    /// <summary>
    /// 把目录引用 <c>ProviderId/ModelId</c> 归一为 wire 上的裸模型 ID。
    /// <para>只剥本 Provider 自己的前缀；不带前缀或前缀属别家时原样返回，
    /// 避免把含 <c>/</c> 的 HuggingFace 风格模型名（如 <c>Qwen/Qwen3-8B</c>）误剥。</para>
    /// </summary>
    private string ToWireModelId(string modelRef)
    {
        var trimmed = modelRef.Trim();
        var prefix = _config.Id + "/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }
}
