using System.Net;

namespace Seeing.Gateway.QQ.Connection;

/// <summary>
/// QQ OpenAPI 调用失败（保留状态码与响应体，供发送 fallback 判断）。
/// </summary>
public sealed class QQApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string Path { get; }
    public string ResponseBody { get; }

    public QQApiException(HttpStatusCode statusCode, string path, string responseBody)
        : base($"QQ API {path} failed: {(int)statusCode} {responseBody}")
    {
        StatusCode = statusCode;
        Path = path;
        ResponseBody = responseBody ?? "";
    }

    public bool IsUrlContentError
    {
        get
        {
            var payload = ResponseBody.ToLowerInvariant();
            return payload.Contains("304003", StringComparison.Ordinal)
                   || payload.Contains("40034028", StringComparison.Ordinal)
                   || payload.Contains("不允许包含url", StringComparison.Ordinal);
        }
    }

    public bool IsMarkdownValidationError
    {
        get
        {
            var code = (int)StatusCode;
            if (code < 400 || code >= 500)
                return false;

            var payload = ResponseBody.ToLowerInvariant();
            return payload.Contains("markdown", StringComparison.Ordinal)
                   || payload.Contains("msg_type", StringComparison.Ordinal)
                   || payload.Contains("msg type", StringComparison.Ordinal)
                   || payload.Contains("message type", StringComparison.Ordinal)
                   || payload.Contains("50056", StringComparison.Ordinal)
                   || payload.Contains("不允许发送原生 markdown", StringComparison.Ordinal)
                   || payload.Contains("40034012", StringComparison.Ordinal);
        }
    }
}
