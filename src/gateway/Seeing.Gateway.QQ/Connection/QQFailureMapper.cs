using Seeing.Agent.Abstractions.Channels;
using System.Net;

namespace Seeing.Gateway.QQ.Connection;

/// <summary>
/// QQ 平台异常 → 通道失败类型。放在 <c>Connection</c> 下供 HTTP 客户端与适配器共用，
/// 避免适配器与 HTTP 客户端相互依赖。
/// </summary>
internal static class QQFailureMapper
{
    internal static OutboundFailureKind MapApiFailure(QQApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return (int)exception.StatusCode == (int)HttpStatusCode.TooManyRequests
            ? OutboundFailureKind.RateLimited
            : OutboundFailureKind.PlatformError;
    }
}
