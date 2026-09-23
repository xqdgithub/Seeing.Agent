using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>question 类型常量</summary>
public static class SystemOneQuestionTypes
{
    public const string Noul = "noul";
    public const string Choice = "choice";
    public const string Score = "score";
}

/// <summary>评估请求：state + model + questions</summary>
public sealed record SystemOneRequest
{
    /// <summary>string | object | array（运行期类型序列化）</summary>
    [JsonPropertyName("state")]     public object? State { get; init; }
    /// <summary>留空时由客户端用 provider 配置的 Model 补全</summary>
    [JsonPropertyName("model")]     public string Model { get; init; } = string.Empty;
    [JsonPropertyName("questions")] public Dictionary<string, SystemOneQuestion> Questions { get; init; } = new();
}

/// <summary>单个 typed question</summary>
public sealed record SystemOneQuestion
{
    [JsonPropertyName("type")]         public string Type { get; init; } = string.Empty;
    [JsonPropertyName("instructions")] public object? Instructions { get; init; }
    [JsonPropertyName("criteria")]     public object? Criteria { get; init; }
}

/// <summary>评估响应</summary>
public sealed record SystemOneResponse
{
    [JsonPropertyName("model")]   public string? Model { get; init; }
    [JsonPropertyName("answers")] public Dictionary<string, SystemOneAnswer> Answers { get; init; } = new();
    [JsonPropertyName("usage")]   public SystemOneUsage? Usage { get; init; }
}

/// <summary>单个应答（按 type 取对应字段）</summary>
public sealed record SystemOneAnswer
{
    [JsonPropertyName("type")]          public string? Type { get; init; }
    [JsonPropertyName("noul")]          public double? Noul { get; init; }
    [JsonPropertyName("choice")]        public string? Choice { get; init; }
    [JsonPropertyName("score")]         public double? Score { get; init; }
    [JsonPropertyName("legend")]        public Dictionary<string, string>? Legend { get; init; }
    [JsonPropertyName("probabilities")] public Dictionary<string, double>? Probabilities { get; init; }
    [JsonPropertyName("confidence")]    public double? Confidence { get; init; }
}

/// <summary>token 用量</summary>
public sealed record SystemOneUsage
{
    [JsonPropertyName("input_tokens")]  public int InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
}

/// <summary>模型条目</summary>
public sealed record SystemOneModel
{
    [JsonPropertyName("name")]         public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")]  public string? Description { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
}

/// <summary>GET /v1/models 响应包装</summary>
public sealed record SystemOneModelsResponse
{
    [JsonPropertyName("models")] public List<SystemOneModel> Models { get; init; } = new();
}
