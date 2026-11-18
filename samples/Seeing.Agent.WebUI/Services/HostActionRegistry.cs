using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Commands;

namespace Seeing.Agent.WebUI.Services;

/// <summary>宿主动作的执行结果。</summary>
public enum HostActionStatus
{
    /// <summary>宿主已处理。</summary>
    Handled,

    /// <summary>宿主认识该动作，但当前上下文不可用（如 ACP 透传下的 model.set）。</summary>
    Unavailable,

    /// <summary>宿主没有注册该动作。</summary>
    NotHandled
}

/// <summary>宿主动作结果（<see cref="Message"/> 由分派器决定是否展示）。</summary>
public sealed record HostActionResult(HostActionStatus Status, string? Message = null);

/// <summary>宿主动作注册表（按会话隔离）。</summary>
public interface IHostActionRegistry
{
    /// <summary>
    /// 注册处理器；返回撤销句柄。同一 <c>(sessionId, actionName)</c> 重复注册时后注册者生效。
    /// </summary>
    IDisposable Register(
        string sessionId,
        string actionName,
        Func<CommandAction, Task<HostActionResult>> handler);

    /// <summary>执行动作；未注册或处理器异常返回 <see cref="HostActionStatus.NotHandled"/>。</summary>
    Task<HostActionResult> InvokeAsync(string sessionId, CommandAction action);
}

/// <summary>
/// WebUI 宿主动作注册表的默认实现。
/// <para>
/// 键为 <c>(sessionId, actionName)</c>：同一 circuit 内的多个会话窗口（会议大屏）不会互相抢动作。
/// 注册/撤销发生在 UI 线程，仍用锁保护，避免后台事件流回调期间的集合竞态。
/// </para>
/// </summary>
public sealed class HostActionRegistry : IHostActionRegistry
{
    private sealed record Entry(long Id, Func<CommandAction, Task<HostActionResult>> Handler);

    private readonly Dictionary<(string SessionId, string ActionName), Entry> _entries = new();
    private readonly object _gate = new();
    private readonly ILogger<HostActionRegistry>? _logger;
    private long _nextId;

    /// <summary>创建注册表；<paramref name="logger"/> 仅用于诊断（测试可省略）。</summary>
    public HostActionRegistry(ILogger<HostActionRegistry>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public IDisposable Register(
        string sessionId,
        string actionName,
        Func<CommandAction, Task<HostActionResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(handler);

        var key = (sessionId, actionName);
        Entry entry;
        var replaced = false;
        lock (_gate)
        {
            replaced = _entries.ContainsKey(key);
            entry = new Entry(++_nextId, handler);
            _entries[key] = entry;
        }

        if (replaced)
        {
            _logger?.LogWarning(
                "宿主动作重复注册，后注册者生效: SessionId={SessionId}, Action={Action}",
                sessionId,
                actionName);
        }

        return new Registration(this, sessionId, actionName, entry.Id);
    }

    /// <inheritdoc />
    public async Task<HostActionResult> InvokeAsync(string sessionId, CommandAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue((sessionId, action.Name), out entry);
        }

        if (entry is null)
            return new HostActionResult(HostActionStatus.NotHandled);

        try
        {
            return await entry.Handler(action).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "宿主动作执行失败，按未处理回落: SessionId={SessionId}, Action={Action}",
                sessionId,
                action.Name);
            return new HostActionResult(HostActionStatus.NotHandled);
        }
    }

    private void Remove(string sessionId, string actionName, long registrationId)
    {
        lock (_gate)
        {
            var key = (sessionId, actionName);
            if (_entries.TryGetValue(key, out var current) && current.Id == registrationId)
                _entries.Remove(key);
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly HostActionRegistry _owner;
        private readonly string _sessionId;
        private readonly string _actionName;
        private readonly long _id;
        private bool _disposed;

        public Registration(HostActionRegistry owner, string sessionId, string actionName, long id)
        {
            _owner = owner;
            _sessionId = sessionId;
            _actionName = actionName;
            _id = id;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.Remove(_sessionId, _actionName, _id);
        }
    }
}
