using Seeing.Agent.Abstractions.Llm;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.TypeSafe.Clients;

/// <summary>
/// TypeSafe（Decision）协议常量：端点与缺省值。
/// <para>provider 连接参数（baseUrl / apiKey / timeout / 代理 / 重试）由框架
/// <see cref="ProviderConfig"/> 承载，这里只保留协议自身的端点事实（spec §7.9）。</para>
/// </summary>
internal static class TypeSafeProtocol
{
    /// <summary>缺省基址；<see cref="ProviderConfig.BaseUrl"/> 未配置时使用。</summary>
    public const string DefaultBaseUrl = "https://api.typesafe.ai";

    /// <summary>typed question 评估端点。</summary>
    public const string EvaluatePath = "/v1/systemone";

    /// <summary>动态模型目录端点。</summary>
    public const string ModelsPath = "/v1/models";

    /// <summary>
    /// <see cref="ProviderConfig.Timeout"/> 非正数时的兜底超时（10s：
    /// 决策调用不应吃满框架 5 分钟缺省超时）。
    /// </summary>
    public const int DefaultTimeoutMs = 10000;

    /// <summary>连接测试探针的问题键。</summary>
    public const string ProbeQuestionId = "probe";
}

/// <summary>POST /v1/systemone 请求体（wire 形状：小写键，TypeSafe 协议约定）。</summary>
internal sealed class DecisionRequestWire
{
    [JsonPropertyName("state")] public object? State { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("questions")] public Dictionary<string, DecisionQuestionWire> Questions { get; set; } = new();
}

/// <summary>单个 typed question 的 wire 形状。</summary>
internal sealed class DecisionQuestionWire
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("instructions")] public object? Instructions { get; set; }
    [JsonPropertyName("criteria")] public object? Criteria { get; set; }
}

/// <summary>POST /v1/systemone 响应体。</summary>
internal sealed class DecisionResponseWire
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("answers")] public Dictionary<string, DecisionAnswerWire> Answers { get; set; } = new();
    [JsonPropertyName("usage")] public DecisionUsageWire? Usage { get; set; }
}

/// <summary>单个应答的 wire 形状（按 type 取对应原语字段）。</summary>
internal sealed class DecisionAnswerWire
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("noul")] public double? Noul { get; set; }
    [JsonPropertyName("choice")] public string? Choice { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("legend")] public Dictionary<string, string>? Legend { get; set; }
    [JsonPropertyName("probabilities")] public Dictionary<string, double>? Probabilities { get; set; }
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
}

/// <summary>token 用量 wire 形状。</summary>
internal sealed class DecisionUsageWire
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
}

/// <summary>GET /v1/models 响应包装。</summary>
internal sealed class ModelsResponseWire
{
    [JsonPropertyName("models")] public List<ModelEntryWire> Models { get; set; } = new();
}

/// <summary>模型目录条目的 wire 形状。</summary>
internal sealed class ModelEntryWire
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
}

/// <summary>
/// 能力端口 DTO ↔ wire 形状映射。
/// <para>wire 类型刻意留在本包内（与 <c>OpenAiJsonModels</c> 同一house 约定）：
/// <see cref="DecisionRequest"/> 等 DTO 不带 wire 注解，协议字段名是各 Provider 的私有事实。</para>
/// </summary>
internal static class TypeSafeWireMap
{
    /// <summary>wire 序列化选项（读时大小写不敏感，写时省略 null）。</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>请求 DTO → wire（model 已由调用方解析为非空）。</summary>
    public static DecisionRequestWire FromRequest(DecisionRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request);

        var questions = new Dictionary<string, DecisionQuestionWire>(StringComparer.Ordinal);
        foreach (var (key, question) in request.Questions)
        {
            questions[key] = new DecisionQuestionWire
            {
                Type = question.Type,
                Instructions = question.Instructions,
                Criteria = question.Criteria
            };
        }

        return new DecisionRequestWire
        {
            State = request.State,
            Model = model,
            Questions = questions
        };
    }

    /// <summary>响应 wire → DTO（usage 只进 <c>llm.after_complete</c> 载荷，不写会话预算——决议 1）。</summary>
    public static DecisionResponse ToResponse(DecisionResponseWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        return new DecisionResponse
        {
            Model = wire.Model,
            Answers = wire.Answers.ToDictionary(
                pair => pair.Key,
                pair => new DecisionAnswer
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
            Usage = wire.Usage is null
                ? null
                : new TokenUsage
                {
                    InputTokens = wire.Usage.InputTokens,
                    OutputTokens = wire.Usage.OutputTokens
                }
        };
    }

    /// <summary>模型目录 wire → <see cref="DecisionModel"/>（<see cref="IDecisionClient"/> 视图）。</summary>
    public static IReadOnlyList<DecisionModel> ToDecisionModels(ModelsResponseWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        return wire.Models
            .Select(m => new DecisionModel
            {
                Name = m.Name,
                Description = m.Description,
                ReleaseDate = m.ReleaseDate
            })
            .ToList();
    }

    /// <summary>
    /// <see cref="DecisionModel"/> → <see cref="ModelConfig"/>（<see cref="IModelCatalogClient"/> 视图，spec §7.3）。
    /// <para>类型固定标 <see cref="ModelType.Decision"/>：动态目录的价值就是让 jev 模型出现在目录里、可被路由。</para>
    /// </summary>
    public static ModelConfig ToModelConfig(DecisionModel model, string providerId)
    {
        ArgumentNullException.ThrowIfNull(model);

        var metadata = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(model.Description))
            metadata["description"] = model.Description;
        if (!string.IsNullOrWhiteSpace(model.ReleaseDate))
            metadata["releaseDate"] = model.ReleaseDate;

        return new ModelConfig
        {
            Id = model.Name,
            Name = model.Name,
            Provider = providerId,
            Types = [ModelType.Decision],
            Metadata = metadata.Count > 0 ? metadata : null
        };
    }
}
