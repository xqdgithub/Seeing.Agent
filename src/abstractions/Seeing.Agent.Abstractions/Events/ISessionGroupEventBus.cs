using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// 会话组变更事件总线：进程内按组 ID 扇出。
/// <para>
/// 用于把会话组（<c>ISessionGroupManager.Changed</c>）的权威快照推送给 UI 订阅方。
/// 事件为普通 record，<b>不</b>实现 <see cref="IMessageEvent"/>，不进执行流。
/// </para>
/// </summary>
public interface ISessionGroupEventBus
{
    /// <summary>发布组变更事件；无订阅者时静默丢弃（不抛）。</summary>
    void Publish(SessionGroupChangedEvent evt);

    /// <summary>订阅指定组的事件流；取消或订阅方释放时自动注销。</summary>
    IAsyncEnumerable<SessionGroupChangedEvent> SubscribeAsync(string groupId, CancellationToken ct);
}

/// <summary>
/// 会话组变更快照事件（普通 record，非执行流事件）。
/// </summary>
public record SessionGroupChangedEvent
{
    /// <summary>组 ID（扇出键）</summary>
    public required string GroupId { get; init; }

    /// <summary>锚点会话 ID</summary>
    public required string AnchorSessionId { get; init; }

    /// <summary>活跃会话 ID（可空，回退锚点）</summary>
    public string? ActiveSessionId { get; init; }

    /// <summary>组版本号（每次变更 +1；订阅方据此丢弃乱序快照）</summary>
    public long Version { get; init; }

    /// <summary>组成员快照</summary>
    public IReadOnlyList<SessionGroupMember> Members { get; init; } = Array.Empty<SessionGroupMember>();

    /// <summary>事件时间戳（UTC）</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
