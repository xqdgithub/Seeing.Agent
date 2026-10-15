using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Core.Host;

/// <summary>宿主请求在途管理器（Singleton）：通用宿主能力调用的在途权威。</summary>
public sealed class HostRequestManager : PendingRequestManager<HostRequest, HostResponse>, IHostRequestManager
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeout;

    /// <summary>创建宿主请求在途管理器（等待超时 30s）。</summary>
    public HostRequestManager(IHostSurfaceRegistry surfaceRegistry, ILogger<HostRequestManager>? logger = null)
        : base(logger, surfaceRegistry)
        => _timeout = DefaultTimeout;

    /// <summary>创建宿主请求在途管理器（可指定超时/收敛宽限；仅供测试注入短值）。</summary>
    internal HostRequestManager(IHostSurfaceRegistry surfaceRegistry, TimeSpan timeout, TimeSpan? convergenceGrace = null, ILogger<HostRequestManager>? logger = null)
        : base(logger, surfaceRegistry, convergenceGrace)
        => _timeout = timeout;

    /// <inheritdoc />
    protected override TimeSpan Timeout => _timeout;

    /// <inheritdoc />
    protected override string GetRequestId(HostRequest request) => request.RequestId;

    /// <inheritdoc />
    protected override HostRequest AssignRequestId(HostRequest request, string id)
    {
        request.RequestId = id;
        return request;
    }

    /// <inheritdoc />
    protected override string GetSessionId(HostRequest request) => request.SessionId;

    /// <inheritdoc />
    protected override HostResponse CreateFallback(HostRequest request, PendingFallbackReason reason) => new()
    {
        RequestId = request.RequestId,
        SessionId = request.SessionId,
        Status = reason switch
        {
            PendingFallbackReason.Timeout => HostResponseStatus.Timeout,
            PendingFallbackReason.Cancelled => HostResponseStatus.Cancelled,
            PendingFallbackReason.Disposed => HostResponseStatus.Unavailable,
            _ => HostResponseStatus.Unavailable
        }
    };

    /// <inheritdoc />
    protected override HostResponse CreateMissingResponse(RequestTicket ticket) => new()
    {
        RequestId = ticket.RequestId,
        Status = HostResponseStatus.Unavailable
    };
}
