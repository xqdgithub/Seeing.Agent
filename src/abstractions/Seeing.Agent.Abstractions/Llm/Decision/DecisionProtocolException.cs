namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// Decision（Jev 风格）协议调用失败。
/// <para>由 <c>TypeSafeDecisionClient</c> 在重试耗尽后抛出；继承了状态码与响应体，
/// 便于上层区分「认证失败」「限流」「协议错误」。</para>
/// <para>命名与原 <c>SystemOneException</c> 对齐（spec §7.9 合流），
/// 但归属 <c>Seeing.Agent.Abstractions.Llm</c>——decision 是 LLM 的一种能力，
/// 不再有独立的 SystemOne 命名空间。</para>
/// </summary>
public sealed class DecisionProtocolException : Exception
{
    /// <summary>HTTP 状态码；纯网络/超时失败（无响应）时为 0。</summary>
    public int StatusCode { get; }

    /// <summary>原始响应体（排障用）；无响应时为 null。</summary>
    public string? ResponseBody { get; }

    /// <summary>构造决策协议异常。</summary>
    public DecisionProtocolException(
        int statusCode,
        string? responseBody,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
