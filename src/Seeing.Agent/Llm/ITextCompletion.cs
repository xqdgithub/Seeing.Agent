namespace Seeing.Agent.Llm;

/// <summary>
/// 非流式文本补全窄接口 — 供插件/Memory 等使用，避免直接依赖完整 <see cref="ILlmService"/>。
/// </summary>
public interface ITextCompletion
{
    /// <summary>
    /// 完成一次文本补全。
    /// </summary>
    /// <param name="systemPrompt">系统提示</param>
    /// <param name="userPrompt">用户提示</param>
    /// <param name="model">模型 ID；空则使用配置的 DefaultModel</param>
    /// <param name="maxTokens">生成上限；null 则使用实现默认值（当前 2048）</param>
    /// <param name="ct">取消令牌</param>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        int? maxTokens = null,
        CancellationToken ct = default);

    /// <summary>
    /// 完成一次文本补全。
    /// </summary>
    /// <param name="systemPrompt">系统提示</param>
    /// <param name="messages">用户提示</param>
    /// <param name="model">模型 ID；空则使用配置的 DefaultModel</param>
    /// <param name="maxTokens">生成上限；null 则使用实现默认值（当前 2048）</param>
    /// <param name="ct">取消令牌</param>
    Task<string> CompleteAsync(
        string systemPrompt,
        List<ChatMessage> messages,
        string? model = null,
        int? maxTokens = null,
        CancellationToken ct = default);
}
