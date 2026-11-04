namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// Decision question 类型常量（Jev 三原语，上线用的 wire 名）。
/// 由原 <c>SystemOneQuestionTypes</c> 去前缀演进而来（spec §7.9）。
/// </summary>
public static class DecisionQuestionTypes
{
    /// <summary>概率原语：某事为真的概率。</summary>
    public const string Noul = "noul";

    /// <summary>选择原语：从候选集中选一项。</summary>
    public const string Choice = "choice";

    /// <summary>打分原语：对对象打分。</summary>
    public const string Score = "score";
}

/// <summary>
/// 决策评估请求：state + model + questions。
/// </summary>
public sealed record DecisionRequest
{
    /// <summary>string | object | array（运行期类型序列化）。</summary>
    public object? State { get; init; }

    /// <summary>模型 ID；留空时由客户端用 Provider 配置的模型补全。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>本次评估的问题集合（键为调用方自定的问题名）。</summary>
    public Dictionary<string, DecisionQuestion> Questions { get; init; } = new();
}

/// <summary>
/// 单个 typed question。
/// </summary>
public sealed record DecisionQuestion
{
    /// <summary>问题类型，取值为 <see cref="DecisionQuestionTypes"/> 之一。</summary>
    public string Type { get; init; } = "";

    /// <summary>指令（运行期类型序列化）。</summary>
    public object? Instructions { get; init; }

    /// <summary>判据（运行期类型序列化）。</summary>
    public object? Criteria { get; init; }
}

/// <summary>
/// 单个应答：按 <see cref="Type"/> 取对应原语字段。
/// </summary>
public sealed record DecisionAnswer
{
    /// <summary>应答类型（回显请求的 Type）。</summary>
    public string? Type { get; init; }

    /// <summary>noul 原语：概率 [0,1]。</summary>
    public double? Noul { get; init; }

    /// <summary>choice 原语：被选中的候选值。</summary>
    public string? Choice { get; init; }

    /// <summary>score 原语：分值。</summary>
    public double? Score { get; init; }

    /// <summary>候选值图例（choice 类问题可回）。</summary>
    public Dictionary<string, string>? Legend { get; init; }

    /// <summary>各候选的概率分布（choice 类问题可回）。</summary>
    public Dictionary<string, double>? Probabilities { get; init; }

    /// <summary>置信度。</summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// 决策评估响应。
/// </summary>
public sealed record DecisionResponse
{
    /// <summary>实际使用的模型 ID。</summary>
    public string? Model { get; init; }

    /// <summary>按问题名索引的应答。</summary>
    public Dictionary<string, DecisionAnswer> Answers { get; init; } = new();

    /// <summary>token 用量；只进 <c>llm.after_complete</c> 载荷，不写会话预算（决议 1）。</summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// 决策模型的目录条目（typesafe 的动态目录，由 Provider 映射为 <see cref="ModelConfig"/>）。
/// </summary>
public sealed record DecisionModel
{
    /// <summary>模型名（同时作为模型 ID 使用）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>描述。</summary>
    public string? Description { get; init; }

    /// <summary>发布日期（原样字符串，格式由 Provider 决定）。</summary>
    public string? ReleaseDate { get; init; }
}
