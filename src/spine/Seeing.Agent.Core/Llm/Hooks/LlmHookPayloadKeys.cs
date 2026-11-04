using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// <c>llm.after_complete</c> 载荷的通用键装配出口。
/// <para>「通用键」= 每种能力都必须出现的 <c>capability</c> / <c>provider</c> / <c>model</c> /
/// <c>usage</c> / <c>duration</c>（spec §7.6 的 result 超集表首四行）。内置适配器与兜底适配器
/// 共用本类，保证两者的通用键形状一致——订阅方（含插件）只读 <see cref="HookDataContract.LlmAfterComplete"/>
/// 的常量，不必为某种能力写特例。</para>
/// <para>本类是纯映射，零业务策略（spec §13 原则 2）。</para>
/// </summary>
internal static class LlmHookPayloadKeys
{
    /// <summary>
    /// 装配通用键。
    /// <para><paramref name="context"/> 允许为 <c>null</c>：兜底适配器的契约是
    /// 「一定有载荷、一定不炸」，上游写错时也只产出空值键，而不是把诊断变成故障源。</para>
    /// </summary>
    /// <param name="context">调用上下文。</param>
    /// <returns>可直接追加能力特化键的可变字典。</returns>
    public static Dictionary<string, object?> Common(LlmPayloadContext? context)
    {
        var capability = context?.Capability ?? LlmCapabilityKind.None;

        return new Dictionary<string, object?>
        {
            // 点分格式的唯一出处：禁止任何调用点手拼字符串（决议 18）
            [HookDataContract.LlmAfterComplete.Capability.Name] = LlmCapabilityMap.ToHookValue(capability),
            [HookDataContract.LlmAfterComplete.Provider.Name] = context?.ProviderId ?? string.Empty,
            [HookDataContract.LlmAfterComplete.Model.Name] = context?.ModelId ?? string.Empty,
            // 改名前的订阅方读的是 modelId：同值写出，避免运行期静默丢字段
            [HookDataContract.LlmAfterComplete.ModelId.Name] = context?.ModelId ?? string.Empty,
            [HookDataContract.LlmAfterComplete.Usage.Name] = context?.Usage,
            [HookDataContract.LlmAfterComplete.Duration.Name] = context?.Duration ?? TimeSpan.Zero,
        };
    }

    /// <summary>
    /// 用量回落：上下文没带 usage 时，从响应 DTO 自带的 usage 取。
    /// <para>非 chat 能力的 usage 只进载荷、不写会话预算（决议 1）。</para>
    /// </summary>
    /// <param name="payload">已装配通用键的字典。</param>
    /// <param name="usageFromResponse">响应 DTO 里的 usage，可为 null。</param>
    public static void FillUsageWhenAbsent(IDictionary<string, object?> payload, TokenUsage? usageFromResponse)
    {
        if (usageFromResponse is null)
            return;

        var key = HookDataContract.LlmAfterComplete.Usage.Name;
        if (payload[key] is TokenUsage)
            return;

        payload[key] = usageFromResponse;
    }
}
