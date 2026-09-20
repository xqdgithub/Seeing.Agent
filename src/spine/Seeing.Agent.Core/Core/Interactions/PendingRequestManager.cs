using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.Core.Interactions;

/// <summary>
/// 泛型在途请求管理器基类：登记/等待/幂等完成/超时/取消/释放的通用机制，领域语义由子类钩子提供。
/// 事件发布不内置，权限/Question 分别在 <see cref="OnRequestBegan"/> / <see cref="OnResolved"/> 中发布各自事件。
/// </summary>
public abstract class PendingRequestManager<TRequest, TResponse>
    : IPendingRequestManager<TRequest, TResponse>
    where TRequest : class
    where TResponse : class
{
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly ILogger? _logger;
    private volatile bool _disposed;

    /// <summary>创建在途管理器；可选注入日志用于订阅者异常隔离记录。</summary>
    protected PendingRequestManager(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>等待超时；默认 5 分钟，子类可覆盖（如测试注入短值）。</summary>
    protected virtual TimeSpan Timeout => TimeSpan.FromMinutes(5);

    /// <summary>读取请求标识；为空时基类分配 Guid 并经 <see cref="AssignRequestId"/> 回写。</summary>
    protected abstract string GetRequestId(TRequest request);

    /// <summary>回写请求标识（值类型/不可变请求须返回新实例）。</summary>
    protected abstract TRequest AssignRequestId(TRequest request, string id);

    /// <summary>读取归属会话标识。</summary>
    protected abstract string GetSessionId(TRequest request);

    /// <summary>超时/取消/释放/呈现端消失时的兜底响应（能拿到 TRequest）。</summary>
    protected abstract TResponse CreateFallback(TRequest request, PendingFallbackReason reason);

    /// <summary>ticket 未命中在途字典（不存在或已完成）时的兜底响应（拿不到 TRequest）。</summary>
    protected abstract TResponse CreateMissingResponse(RequestTicket ticket);

    /// <summary>
    /// 登记前校验；返回非 null 表示立即完成（如权限溢出拒绝）。
    /// 基类负责登记、完成 TCS、触发 <see cref="PendingChanged"/>、调用 <see cref="OnResolved"/>。
    /// </summary>
    protected virtual TResponse? ValidateOnBegin(TRequest request) => null;

    /// <summary>登记成功后调用（发布请求事件）。即时完成分支不调用。</summary>
    protected virtual void OnRequestBegan(TRequest request) { }

    /// <summary>决议完成后调用（发布决议事件 / 广播 Dismiss / 重评估通知）；须幂等。</summary>
    protected virtual void OnResolved(TRequest request, TResponse response) { }

    /// <inheritdoc />
    public event Action? PendingChanged;

    /// <inheritdoc />
    public Task<RequestTicket> BeginAsync(TRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);

        var requestId = GetRequestId(request);
        if (string.IsNullOrEmpty(requestId))
        {
            requestId = Guid.NewGuid().ToString("N");
            request = AssignRequestId(request, requestId);
        }

        var immediate = ValidateOnBegin(request);
        var entry = new PendingEntry(
            request,
            new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously));

        if (IsImmediate(immediate))
        {
            entry.Completion.TrySetResult(immediate!);
            _pending[requestId] = entry;
            RaisePendingChanged();
            OnResolved(request, immediate!);
            return Task.FromResult(new RequestTicket(requestId, GetSessionId(request)));
        }

        _pending[requestId] = entry;
        RaisePendingChanged();
        OnRequestBegan(request);
        return Task.FromResult(new RequestTicket(requestId, GetSessionId(request)));
    }

    /// <inheritdoc />
    public async Task<TResponse> WaitAsync(RequestTicket ticket, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(ticket.RequestId, out var entry))
            return CreateMissingResponse(ticket);

        using var registration = ct.Register(() =>
            TryResolve(ticket.RequestId, CreateFallback(entry.Request, PendingFallbackReason.Cancelled)));

        var completed = await Task.WhenAny(entry.Completion.Task, Task.Delay(Timeout, CancellationToken.None))
            .ConfigureAwait(false);

        if (completed != entry.Completion.Task)
            TryResolve(ticket.RequestId, CreateFallback(entry.Request, PendingFallbackReason.Timeout));

        var response = await entry.Completion.Task.ConfigureAwait(false);
        _pending.TryRemove(new KeyValuePair<string, PendingEntry>(ticket.RequestId, entry));
        return response;
    }

    /// <inheritdoc />
    public bool TryResolve(string requestId, TResponse response, string? expectedSessionId = null)
    {
        if (string.IsNullOrEmpty(requestId) || !_pending.TryGetValue(requestId, out var entry))
            return false;

        if (!string.IsNullOrEmpty(expectedSessionId) &&
            !string.Equals(GetSessionId(entry.Request), expectedSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!entry.Completion.TrySetResult(response))
            return false;

        OnResolved(entry.Request, response);
        RaisePendingChanged();
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<TRequest> GetPending(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<TRequest>();

        return _pending.Values
            .Where(entry => !entry.Completion.Task.IsCompleted)
            .Where(entry => string.Equals(GetSessionId(entry.Request), sessionId, StringComparison.Ordinal))
            .Select(entry => entry.Request)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<TRequest> GetAllPending() =>
        _pending.Values
            .Where(entry => !entry.Completion.Task.IsCompleted)
            .Select(entry => entry.Request)
            .ToList();

    /// <inheritdoc />
    public int PendingCount => _pending.Values.Count(entry => !entry.Completion.Task.IsCompleted);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            OnDisposing();

            foreach (var requestId in _pending.Keys)
            {
                if (!_pending.TryGetValue(requestId, out var entry))
                    continue;

                try
                {
                    TryResolve(requestId, CreateFallback(entry.Request, PendingFallbackReason.Disposed));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "释放在途请求 {RequestId} 时异常", requestId);
                }
            }
        }
        finally
        {
            _pending.Clear();
            RaisePendingChanged();
        }
    }

    /// <summary>释放前子类清理钩子（退订事件、释放 Timer 等）。</summary>
    protected virtual void OnDisposing() { }

    private static bool IsImmediate(TResponse? value) => value is not null;

    private void RaisePendingChanged()
    {
        var handlers = PendingChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "在途请求变更通知订阅者异常");
            }
        }
    }

    private sealed record PendingEntry(
        TRequest Request,
        TaskCompletionSource<TResponse> Completion);
}
