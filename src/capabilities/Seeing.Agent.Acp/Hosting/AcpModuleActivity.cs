namespace Seeing.Agent.Acp.Hosting;

/// <summary>
/// ACP 模块运行门控 — Activate/Deactivate 与 HostedService 长循环共享。
/// </summary>
public sealed class AcpModuleActivity
{
    // 默认 true：无 Module 生命周期时 HostedService 仍可运行；Deactivate 翻 false。
    private volatile bool _active = true;
    private readonly List<Action> _wakeHandlers = [];
    private readonly object _gate = new();

    /// <summary>模块是否处于激活运行态（可被 HostedService 长循环观察）。</summary>
    public bool IsActive => _active;

    /// <summary>Activate 时置位。</summary>
    public void MarkActive() => _active = true;

    /// <summary>Deactivate：翻标志并唤醒已登记的 Channel/等待方。</summary>
    public void MarkInactiveAndWake()
    {
        _active = false;
        Action[] handlers;
        lock (_gate)
            handlers = _wakeHandlers.ToArray();
        foreach (var handler in handlers)
            handler();
    }

    /// <summary>登记 Deactivate 时的唤醒回调（如写 Channel 哨兵）。</summary>
    public void RegisterWake(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _wakeHandlers.Add(handler);
    }
}
