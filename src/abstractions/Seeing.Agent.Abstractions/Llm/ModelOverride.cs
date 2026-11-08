using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型元数据用户覆盖节（<c>ModelOverrides</c>，用户级 <c>~/.seeing/model-overrides.json</c>）。
/// <para>节内容即字典：键 = <c>providerId/modelId</c>（与模型目录 catalog key 一致）；值 = 稀疏补丁。</para>
/// <para>仅显式设置的字段参与覆盖；未设置的字段跟随远端/默认值，远端刷新后仍可更新。</para>
/// <para>键在目录中不存在时视为补录（新增模型）。</para>
/// </summary>
/// <remarks>节类型为 <c>Dictionary&lt;string, ModelOverride&gt;</c>（文件根即字典，与 Providers 节一致）。</remarks>
public class ModelOverride
{
    /// <summary>用途类型（多标签）。</summary>
    [JsonPropertyName("types")]
    public List<ModelType>? Types { get; set; }

    /// <summary>显示名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>上下文上限（token）。</summary>
    [JsonPropertyName("contextLimit")]
    public int? ContextLimit { get; set; }

    /// <summary>最大输出（token）。</summary>
    [JsonPropertyName("outputLimit")]
    public int? OutputLimit { get; set; }

    /// <summary>输入模态（如 text、image）。</summary>
    [JsonPropertyName("inputModalities")]
    public List<string>? InputModalities { get; set; }

    /// <summary>输出模态。</summary>
    [JsonPropertyName("outputModalities")]
    public List<string>? OutputModalities { get; set; }

    /// <summary>是否免费模型。</summary>
    [JsonPropertyName("isFree")]
    public bool? IsFree { get; set; }

    /// <summary>
    /// 思考强度覆盖。<c>null</c> = 回退远端/能力目录声明；
    /// <see cref="ModelThinkingOverride.Supported"/> 为 <c>false</c> = 显式关闭（不回退）。
    /// </summary>
    [JsonPropertyName("thinking")]
    public ModelThinkingOverride? Thinking { get; set; }

    /// <summary>是否所有字段均为空（无可覆盖内容）。</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Types is null &&
        Name is null &&
        ContextLimit is null &&
        OutputLimit is null &&
        InputModalities is null &&
        OutputModalities is null &&
        IsFree is null &&
        Thinking is null;

    /// <summary>复制补丁（深拷贝列表与思考覆盖）。</summary>
    public ModelOverride Clone() => new()
    {
        Types = Types is null ? null : new List<ModelType>(Types),
        Name = Name,
        ContextLimit = ContextLimit,
        OutputLimit = OutputLimit,
        InputModalities = InputModalities is null ? null : new List<string>(InputModalities),
        OutputModalities = OutputModalities is null ? null : new List<string>(OutputModalities),
        IsFree = IsFree,
        Thinking = Thinking?.Clone()
    };
}

/// <summary>
/// 思考强度覆盖（仅覆盖层使用；不复用 <see cref="ThinkingOptions"/>，避免 legacy
/// <c>type</c> / <c>budgetTokens</c> 被无差别写出）。
/// <para><b>三态</b>：覆盖层无此键 = 跟随下层声明；<see cref="Supported"/> = <c>false</c> =
/// 「本模型不声明思考能力」；<c>true</c> = 按本对象整块替换。</para>
/// <para><b>关闭思考</b>用 <see cref="CreateOff"/> 表达（<c>Supported=true</c> +
/// <c>Default</c> 指向关闭档），不要用 <c>Supported=false</c>——后者请求侧会清空
/// <c>ThinkingEffort</c>，OpenAI 兼容出站反而缺省开启。</para>
/// </summary>
public sealed class ModelThinkingOverride
{
    /// <summary>是否支持思考。设为 false 即显式关闭该模型的思考能力。</summary>
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    /// <summary>默认档 key（落在 <see cref="Levels"/> 内才有意义；不填则未选档时不传思考字段）。</summary>
    [JsonPropertyName("default")]
    public string? Default { get; set; }

    /// <summary>档位表（整块替换下层声明）。</summary>
    [JsonPropertyName("levels")]
    public List<ThinkingLevel>? Levels { get; set; }

    /// <summary>OpenAI 兼容思考回传字段名；仅 <c>reasoning_content</c> 生效。</summary>
    [JsonPropertyName("interleaved")]
    public string? Interleaved { get; set; }

    /// <summary>
    /// 构造「模型级关闭思考」覆盖：保留原有档位并确保含关闭档，把默认档指向关闭档。
    /// <para><b>为什么不是 <c>Supported=false</c></b>：后者表示「该模型不声明思考能力」，
    /// 请求侧会清空 <c>ThinkingEffort</c>，OpenAI 兼容出站会落进「缺省开启」分支，
    /// 结果用户以为关了、实际仍在思考。关闭必须靠关闭档 key 出站。</para>
    /// </summary>
    /// <param name="existing">当前生效声明（可为 null）。档位与回传字段原样保留。</param>
    public static ModelThinkingOverride CreateOff(ThinkingOptions? existing)
    {
        var levels = existing?.Levels is { Count: > 0 } declared
            ? declared.Select(level => new ThinkingLevel
            {
                Key = level.Key,
                Label = level.Label,
                BudgetTokens = level.BudgetTokens
            }).ToList()
            : [];

        if (!levels.Any(level => ThinkingEffortKeys.IsOff(level.Key)))
        {
            levels.Insert(0, new ThinkingLevel
            {
                Key = ThinkingEffortKeys.DisabledKey,
                Label = ThinkingEffortKeys.DisabledLabel
            });
        }

        return new ModelThinkingOverride
        {
            Supported = true,
            Default = levels.First(level => ThinkingEffortKeys.IsOff(level.Key)).Key,
            Interleaved = existing?.Interleaved,
            Levels = levels
        };
    }

    /// <summary>本覆盖是否表达「模型级默认关闭思考」。</summary>
    public bool IsOff => Supported && ThinkingEffortKeys.IsOff(Default);

    /// <summary>深拷贝。</summary>
    public ModelThinkingOverride Clone() => new()
    {
        Supported = Supported,
        Default = Default,
        Interleaved = Interleaved,
        Levels = Levels?.Select(level => new ThinkingLevel
        {
            Key = level.Key,
            Label = level.Label,
            BudgetTokens = level.BudgetTokens
        }).ToList()
    };
}
