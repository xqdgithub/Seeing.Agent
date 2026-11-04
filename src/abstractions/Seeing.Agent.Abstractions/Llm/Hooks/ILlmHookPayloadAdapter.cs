namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// <c>llm.after_complete</c> 载荷组装适配器（可注册，spec §7.5）。
/// <para>为什么要有它：Core 不能认识每种能力的响应 DTO，否则每加一种能力都要改脊柱
/// （spec §13 原则 2）。适配器把"响应 → result 字典"的语义解释交回能力方，
/// 插件注册同 <see cref="Capability"/> 的适配器即<b>后注册胜</b>覆盖内置。</para>
/// <para>已知限制：<see cref="LlmCapabilityKind"/> 是封闭枚举，适配器只能覆盖既有能力的载荷形状，
/// 不能定义全新能力。</para>
/// </summary>
public interface ILlmHookPayloadAdapter
{
    /// <summary>本适配器负责的能力（单一值；组合能力需注册多个适配器）。</summary>
    LlmCapabilityKind Capability { get; }

    /// <summary>按能力把响应组装成 <c>llm.after_complete</c> 的 result 字典。</summary>
    IReadOnlyDictionary<string, object?> Build(LlmPayloadContext context);
}

/// <summary>
/// 组装 <c>llm.after_complete</c> 载荷所需的上下文。
/// </summary>
public sealed record LlmPayloadContext
{
    /// <summary>本次调用的能力。</summary>
    public required LlmCapabilityKind Capability { get; init; }

    /// <summary>Provider ID。</summary>
    public required string ProviderId { get; init; }

    /// <summary>模型 ID。</summary>
    public required string ModelId { get; init; }

    /// <summary>各能力的响应 DTO（由适配器按 <see cref="Capability"/> 解释）。</summary>
    public object? Response { get; init; }

    /// <summary>token 用量；非 chat 能力只进载荷、不写会话预算（决议 1）。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>调用耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>会话 ID；无会话归属时为 null。</summary>
    public string? SessionId { get; init; }

    /// <summary>是否来自流式调用。</summary>
    public bool Streaming { get; init; }
}
