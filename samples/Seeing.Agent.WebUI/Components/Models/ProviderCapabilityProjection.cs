using Seeing.Agent.Abstractions.Llm;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Components.Models;

/// <summary>Provider 能力的来源三态（决定"这个能力从哪来的"这句话怎么说）。</summary>
public enum ProviderCapabilitySource
{
    /// <summary>配置未写 <c>capabilities</c>：扩展 Provider 用插件作者默认值，配置型 Provider 用协议缺省。</summary>
    Default,

    /// <summary>配置显式写了非空清单：以配置为准（只供出勾选项）。</summary>
    Declared,

    /// <summary>配置显式写了空清单：Provider 不供出任何能力（仅能由手写 JSON 产生）。</summary>
    Disabled,
}

/// <summary>
/// 一个 Provider 的能力投影：运行时生效位集 + 配置声明来源。
/// <para><b>为什么需要它</b>：Provider 卡片里的能力多选框显示的是<em>配置覆盖项</em>，
/// 而不是生效能力。扩展 Provider 未写覆盖时该多选框必然为空，用户会误读成
/// 「这个 Provider 没有声明任何能力」——实际上它可能正在供出 chat + embedding。
/// 生效能力的唯一真相是 <see cref="ProviderInfo.Capabilities"/>（运行时投影）。</para>
/// </summary>
/// <param name="Effective">运行时真正供出的能力位集。</param>
/// <param name="DeclaredMask">配置清单解析出的位集（未登记的名字不计入）。</param>
/// <param name="DeclaredKeys">配置里的原始能力键（归一为小驼峰，去空去重）。</param>
/// <param name="UnknownKeys">配置里未登记的能力键（Provider 侧会记警告并忽略）。</param>
/// <param name="Source">能力来源三态。</param>
public sealed record ProviderCapabilityView(
    LlmCapabilityKind Effective,
    LlmCapabilityKind DeclaredMask,
    IReadOnlyList<string> DeclaredKeys,
    IReadOnlyList<string> UnknownKeys,
    ProviderCapabilitySource Source)
{
    /// <summary>生效能力的界面文案，如 <c>对话（chat）、向量（embedding）</c>；无能力时为 <c>（无）</c>。</summary>
    public string EffectiveText => DescribeMask(Effective);

    /// <summary>配置声明的界面文案（<see cref="ProviderCapabilitySource.Default"/> 时为空串）。</summary>
    public string DeclaredText => DescribeMask(DeclaredMask);

    /// <summary>
    /// 配置声明与运行时不一致（只告警不拦截：Provider 可能主动裁掉未实现的组合）。
    /// <para>未登记的能力键也算不一致：用户写了但没生效，必须看得见。</para>
    /// </summary>
    public bool HasDrift =>
        Source == ProviderCapabilitySource.Declared &&
        (DeclaredMask != Effective || UnknownKeys.Count > 0);

    /// <summary>不一致的具体说明；<see cref="HasDrift"/> 为 false 时为空串。</summary>
    public string DriftText
    {
        get
        {
            if (!HasDrift)
                return string.Empty;

            return UnknownKeys.Count > 0
                ? $"配置里有未登记的能力键（{string.Join(", ", UnknownKeys)}），已忽略；实际生效 {EffectiveText}。"
                : $"配置声明 {DeclaredText}，但运行时实际供出 {EffectiveText}。";
        }
    }

    /// <summary>来源文案：把"当默认值用的是什么"说清楚（插件默认 ≠ 协议缺省）。</summary>
    public string SourceText(string? protocol, ProviderOrigin origin)
    {
        return Source switch
        {
            ProviderCapabilitySource.Declared => "来源：配置覆盖",
            ProviderCapabilitySource.Disabled => "来源：配置已关闭全部能力",
            _ when origin == ProviderOrigin.Extension => "来源：插件默认",
            _ => string.IsNullOrWhiteSpace(protocol)
                ? "来源：协议缺省"
                : $"来源：协议缺省（{protocol}）",
        };
    }

    private static string DescribeMask(LlmCapabilityKind mask) =>
        mask == LlmCapabilityKind.None
            ? "（无）"
            : string.Join("、", CapabilityLabels.Singles(mask).Select(CapabilityLabels.Label));
}

/// <summary>
/// <see cref="ProviderCapabilityView"/> 的构造与值归一（纯函数，便于单测）。
/// </summary>
public static class ProviderCapabilityProjection
{
    /// <summary>
    /// 构造能力投影。
    /// </summary>
    /// <param name="info">Provider 运行时信息（<see cref="ProviderInfo.Capabilities"/> 是生效能力的唯一真相）；未选中时为 null。</param>
    /// <param name="declaredRaw">
    /// 配置里的能力清单原始值：<c>null</c> 表示未声明；<see cref="List{T}"/> / <c>string[]</c> /
    /// <see cref="JsonElement"/> 数组等均可（配置从 JSON 读回时是 <see cref="JsonElement"/>）。
    /// </param>
    public static ProviderCapabilityView Build(ProviderInfo? info, object? declaredRaw)
    {
        var effective = info?.Capabilities ?? LlmCapabilityKind.None;
        var keys = ReadDeclaredKeys(declaredRaw);

        // 无法区分"未声明"与"声明为空"时按未声明处理：能区分时以 null 判定。
        if (!IsDeclared(declaredRaw))
            return new ProviderCapabilityView(Effective: effective, DeclaredMask: LlmCapabilityKind.None,
                DeclaredKeys: [], UnknownKeys: [], Source: ProviderCapabilitySource.Default);

        if (keys.Count == 0)
            return new ProviderCapabilityView(Effective: effective, DeclaredMask: LlmCapabilityKind.None,
                DeclaredKeys: [], UnknownKeys: [], Source: ProviderCapabilitySource.Disabled);

        var mask = LlmCapabilityKind.None;
        var unknown = new List<string>();
        foreach (var key in keys)
        {
            if (LlmCapabilityMap.TryParseConfigKey(key, out var kind))
                mask |= kind;
            else
                unknown.Add(key);
        }

        return new ProviderCapabilityView(
            Effective: effective,
            DeclaredMask: mask,
            DeclaredKeys: keys,
            UnknownKeys: unknown,
            Source: ProviderCapabilitySource.Declared);
    }

    /// <summary>
    /// 把配置里的能力清单归一为小驼峰键集合（去空、去重、大小写不敏感）。
    /// <para>未登记的名字<em>原样保留</em>：UI 不做合法性裁决，交给 Provider 记警告并忽略，
    /// 这样用户能看见自己写错了什么，而不是被悄悄吞掉。</para>
    /// </summary>
    public static IReadOnlyList<string> ReadDeclaredKeys(object? declaredRaw)
    {
        var candidates = declaredRaw switch
        {
            null => [],
            JsonElement { ValueKind: JsonValueKind.Array } array =>
                array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : e.ToString()),
            JsonElement { ValueKind: JsonValueKind.String } text => [text.GetString()],
            JsonElement { ValueKind: JsonValueKind.Null } => [],
            string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IEnumerable<string?> strings => strings,
            _ => [],
        };

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim())
            .Select(candidate => LlmCapabilityMap.TryParseConfigKey(candidate, out var kind)
                ? LlmCapabilityMap.ToConfigKey(kind)
                : candidate)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 配置是否写了能力清单（含显式空数组）。<c>null</c> 与"键不存在"都算未声明。
    /// </summary>
    public static bool IsDeclared(object? declaredRaw)
    {
        return declaredRaw switch
        {
            null => false,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => false,
            _ => true,
        };
    }
}
