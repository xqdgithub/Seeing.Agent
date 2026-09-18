using System.Linq;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Hosting;

/// <summary>
/// 会话组事件桥：把 <see cref="ISessionGroupManager.Changed"/> 的权威快照
/// 转发到 <see cref="ISessionGroupEventBus"/>，供 UI 订阅方消费。
/// </summary>
public sealed class SessionGroupEventBridge : IHostedService
{
    private readonly ISessionGroupManager _groupManager;
    private readonly ISessionGroupEventBus _bus;

    /// <summary>创建桥接服务。</summary>
    public SessionGroupEventBridge(ISessionGroupManager groupManager, ISessionGroupEventBus bus)
    {
        _groupManager = groupManager;
        _bus = bus;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _groupManager.Changed += OnGroupChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _groupManager.Changed -= OnGroupChanged;
        return Task.CompletedTask;
    }

    private void OnGroupChanged(object? sender, SessionGroupChangedEventArgs e)
    {
        var group = e.Group;
        _bus.Publish(new SessionGroupChangedEvent
        {
            GroupId = group.Id,
            AnchorSessionId = group.AnchorSessionId,
            ActiveSessionId = group.ActiveSessionId,
            Version = group.Version,
            Members = group.Members.ToList()
        });
    }
}
