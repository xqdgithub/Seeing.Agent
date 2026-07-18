namespace Seeing.Agent.Llm;

/// <summary>
/// 非流式文本补全窄接口 — 供插件/Memory 等使用，避免直接依赖完整 <see cref="ILlmService"/>。
/// </summary>
public interface ITextCompletion
{
    /// <summary>
    /// 完成一次文本补全。model 为空时使用配置的 DefaultModel。
    /// </summary>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        CancellationToken ct = default);
}
