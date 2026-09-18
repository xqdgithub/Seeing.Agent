using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Hosting;
using Seeing.Session.Core;
using Xunit;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;

namespace Seeing.Agent.Tests.Sessions;

/// <summary>
/// Task 9：会话组事件总线（进程内扇出）与会话组管理器 → 总线桥接。
/// <para><see cref="SessionGroupChangedEvent"/> 为普通 record，不进执行流。</para>
/// </summary>
public class SessionGroupEventBusTests
{
    [Fact]
    public async Task Publish_WithSubscriber_ShouldDeliverEvent()
    {
        var bus = new ChannelSessionGroupEventBus();
        var evt = new SessionGroupChangedEvent
        {
            GroupId = "g1",
            AnchorSessionId = "s1",
            Version = 3
        };

        var received = await CollectOneAsync(bus, "g1", () => bus.Publish(evt));

        received.GroupId.Should().Be("g1");
        received.Version.Should().Be(3);
    }

    [Fact]
    public void Publish_WithoutSubscriber_ShouldNotThrow()
    {
        var bus = new ChannelSessionGroupEventBus();
        var act = () => bus.Publish(new SessionGroupChangedEvent
        {
            GroupId = "no-subscriber",
            AnchorSessionId = "s1"
        });

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Bridge_OnManagerChanged_ShouldPublishGroupSnapshot()
    {
        var manager = new Mock<ISessionGroupManager>();
        var bus = new ChannelSessionGroupEventBus();
        var bridge = new SessionGroupEventBridge(manager.Object, bus);
        await bridge.StartAsync(CancellationToken.None);

        var group = new SessionGroup
        {
            Id = "g1",
            AnchorSessionId = "anchor",
            ActiveSessionId = "child",
            Version = 7,
            Members = new List<SessionGroupMember>
            {
                new() { SessionId = "anchor", IsAnchor = true },
                new() { SessionId = "child", Relation = SessionRelation.Child }
            }
        };

        SessionGroupChangedEvent? received = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // SubscribeAsync 在调用时同步注册，先订阅再启动读取循环，避免发布丢失。
        var stream = bus.SubscribeAsync("g1", cts.Token);
        var reader = Task.Run(async () =>
        {
            await foreach (var e in stream)
            {
                received = e;
                break;
            }
        });
        manager.Raise(m => m.Changed += null, new SessionGroupChangedEventArgs { Group = group });

        await reader;
        received.Should().NotBeNull();
        received!.GroupId.Should().Be("g1");
        received.AnchorSessionId.Should().Be("anchor");
        received.ActiveSessionId.Should().Be("child");
        received.Version.Should().Be(7);
        received.Members.Should().HaveCount(2);
    }

    [Fact]
    public async Task Bridge_StopAsync_ShouldUnsubscribeFromManager()
    {
        var manager = new Mock<ISessionGroupManager>();
        var bus = new ChannelSessionGroupEventBus();
        var bridge = new SessionGroupEventBridge(manager.Object, bus);
        await bridge.StartAsync(CancellationToken.None);

        var group = new SessionGroup { Id = "g1", AnchorSessionId = "a", Version = 1 };

        var received = new List<SessionGroupChangedEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = bus.SubscribeAsync("g1", cts.Token);
        var reader = Task.Run(async () =>
        {
            await foreach (var e in stream)
                received.Add(e);
        });

        // 停止前：Changed 应桥接为总线事件
        manager.Raise(m => m.Changed += null, new SessionGroupChangedEventArgs { Group = group });
        await WaitUntilAsync(() => received.Count >= 1);
        received.Should().HaveCount(1);

        await bridge.StopAsync(CancellationToken.None);

        // 停止后：Changed 不再桥接；直接 Publish 的哨兵应为下一个到达事件（证明未混入 Changed）
        manager.Raise(m => m.Changed += null, new SessionGroupChangedEventArgs { Group = group });
        bus.Publish(new SessionGroupChangedEvent
        {
            GroupId = "g1",
            AnchorSessionId = "sentinel",
            Version = 99
        });
        await WaitUntilAsync(() => received.Count >= 2);

        received.Should().HaveCount(2);
        received[1].AnchorSessionId.Should().Be("sentinel");

        cts.Cancel();
        try { await reader; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Subscribe_WhenLastSubscriberRemoved_ShouldDropGroupKey()
    {
        var bus = new ChannelSessionGroupEventBus();
        using var cts = new CancellationTokenSource();
        var stream = bus.SubscribeAsync("g1", cts.Token);
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in stream) { }
        });

        await WaitUntilAsync(() => SubscriberKeys(bus).Contains("g1"));

        cts.Cancel();
        try { await reader; } catch (OperationCanceledException) { }

        await WaitUntilAsync(() => !SubscriberKeys(bus).Contains("g1"));
    }

    private static IReadOnlyCollection<string> SubscriberKeys(ChannelSessionGroupEventBus bus)
    {
        var field = typeof(ChannelSessionGroupEventBus).GetField(
            "_subscribers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (ConcurrentDictionary<string, List<Channel<SessionGroupChangedEvent>>>)field.GetValue(bus)!;
        return dict.Keys.ToList();
    }

    private static async Task<SessionGroupChangedEvent> CollectOneAsync(
        ISessionGroupEventBus bus, string groupId, Action publish)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SessionGroupChangedEvent? received = null;
        var stream = bus.SubscribeAsync(groupId, cts.Token);
        var reader = Task.Run(async () =>
        {
            await foreach (var e in stream)
            {
                received = e;
                break;
            }
        });

        publish();
        await reader;
        return received!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等待条件超时");
            await Task.Delay(10);
        }
    }
}
