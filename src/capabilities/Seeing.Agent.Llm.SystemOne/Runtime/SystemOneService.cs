using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.Llm.SystemOne.Runtime;

/// <summary>
/// SystemOne 业务门面：改为 <see cref="IDecisionClient"/> 的调用方，经 <see cref="ILlmRouter"/> 解析客户端
/// （spec §7.9）。
/// <para>原「按默认 provider 转发」的自有注册表已删除：provider 真相只剩框架的
/// <c>Providers.typesafe</c>，模型真相只剩 <c>DefaultModels</c> 与模型目录。</para>
/// </summary>
public sealed class SystemOneService : ISystemOneService
{
    private readonly ILlmRouter _router;
    private readonly IModelConfigManager _models;

    /// <summary>注入路由与模型目录（均由 Core 提供实现，本包只依赖契约）。</summary>
    public SystemOneService(ILlmRouter router, IModelConfigManager models)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    /// <inheritdoc />
    public async Task<SystemOneResponse> EvaluateAsync(
        SystemOneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = DecisionModelResolver.Resolve(_models, request.Model);
        var client = _router.Resolve<IDecisionClient>(modelId);

        var response = await client.EvaluateAsync(
            ToDecisionRequest(request, modelId),
            call: null,
            cancellationToken).ConfigureAwait(false);

        return ToSystemOneResponse(response);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var client = _router.Resolve<IDecisionClient>(ResolveModelForProvider(providerId));
        var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);

        return models
            .Select(m => new SystemOneModel
            {
                Name = m.Name,
                Description = m.Description,
                ReleaseDate = m.ReleaseDate
            })
            .ToList();
    }

    /// <summary>
    /// 选定用于取目录的模型 ID。
    /// <para>Router 按 <c>(providerId, kind)</c> 缓存客户端，同一 Provider 的任一 Decision 模型都返回同一实例，
    /// 因此这里排序取首不影响结果，只为拿到该 Provider 的客户端。</para>
    /// </summary>
    private string ResolveModelForProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return DecisionModelResolver.Resolve(_models, null);

        var candidates = _models.GetModelsByType(ModelType.Decision, providerId)
            .Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Provider '{providerId}' 未提供可路由的 Decision 模型"
                + "（检查其 capabilities 是否含 decision，以及模型 types 是否标为 decision）");

        return candidates[0];
    }

    private static DecisionRequest ToDecisionRequest(SystemOneRequest request, string modelId)
        => new()
        {
            State = request.State,
            Model = modelId,
            Questions = request.Questions.ToDictionary(
                pair => pair.Key,
                pair => new DecisionQuestion
                {
                    Type = pair.Value.Type,
                    Instructions = pair.Value.Instructions,
                    Criteria = pair.Value.Criteria
                },
                StringComparer.Ordinal)
        };

    private static SystemOneResponse ToSystemOneResponse(DecisionResponse response)
        => new()
        {
            Model = response.Model,
            Answers = response.Answers.ToDictionary(
                pair => pair.Key,
                pair => new SystemOneAnswer
                {
                    Type = pair.Value.Type,
                    Noul = pair.Value.Noul,
                    Choice = pair.Value.Choice,
                    Score = pair.Value.Score,
                    Legend = pair.Value.Legend,
                    Probabilities = pair.Value.Probabilities,
                    Confidence = pair.Value.Confidence
                },
                StringComparer.Ordinal),
            Usage = response.Usage is null
                ? null
                : new SystemOneUsage
                {
                    InputTokens = response.Usage.InputTokens,
                    OutputTokens = response.Usage.OutputTokens
                }
        };
}
