namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne API 调用失败（最终重试耗尽后抛出）</summary>
public sealed class SystemOneException : Exception
{
    /// <summary>HTTP 状态码；纯网络/超时失败（无响应）时为 0</summary>
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public SystemOneException(int statusCode, string? responseBody, string message, Exception? inner = null)
        : base(message, inner) { StatusCode = statusCode; ResponseBody = responseBody; }
}
