namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 一次 LLM 调用的横切选项：会话归属与是否发 Hook。
/// <para>默认发 Hook；旁路调用（如 <c>TextCompletionService</c> 的标题生成）显式置
/// <see cref="EmitHooks"/> = <c>false</c>。</para>
/// </summary>
public sealed record LlmInvocationOptions
{
    /// <summary>会话 ID。为空表示无会话归属（后台 worker / 无会话工具），非 chat 能力不写会话预算。</summary>
    public string? SessionId { get; init; }

    /// <summary>父会话 ID（子 Agent 场景）。</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>是否触发 <c>llm.*</c> Hook；默认触发。</summary>
    public bool EmitHooks { get; init; } = true;
}
