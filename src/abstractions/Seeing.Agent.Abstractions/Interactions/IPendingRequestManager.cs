namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>
/// 泛型在途请求管理器契约：登记/等待/幂等完成/清理，供权限审批、Question 等交互领域复用。
/// </summary>
public interface IPendingRequestManager<TRequest, TResponse> : IDisposable
    where TRequest : class
    where TResponse : class
{
    /// <summary>登记在途请求（分配 RequestId）并返回票据。</summary>
    Task<RequestTicket> BeginAsync(TRequest request, CancellationToken ct = default);

    /// <summary>等待决议；未命中在途字典返回兜底缺失响应；超时/取消返回对应兜底响应。</summary>
    Task<TResponse> WaitAsync(RequestTicket ticket, CancellationToken ct = default);

    /// <summary>幂等完成；expectedSessionId 非空时校验归属。</summary>
    bool TryResolve(string requestId, TResponse response, string? expectedSessionId = null);

    IReadOnlyList<TRequest> GetPending(string sessionId);

    /// <summary>全部未完成在途请求（不做会话过滤）。</summary>
    IReadOnlyList<TRequest> GetAllPending();

    int PendingCount { get; }

    /// <summary>在途集合变化（尽力通知；订阅者异常须隔离）。</summary>
    event Action? PendingChanged;
}
