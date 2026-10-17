using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Execution;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class ExecutionEventPublisherLiveTests
{
    private static readonly IMessageEvent[] Events =
    [
        new LoopStartEvent { SessionId = "s1", LoopId = "l1" },
        new StreamDeltaEvent { SessionId = "s1", LoopId = "l1", ContentDelta = "hello" },
        new LoopCompleteEvent { SessionId = "s1", LoopId = "l1", Success = true }
    ];

    [Fact]
    public async Task SubscribeLive_PublishAfterRegistration_ShouldReceiveLiveEvents()
    {
        var publisher = CreatePublisher();

        using var subscription = publisher.SubscribeLive("s1");

        foreach (var evt in Events)
            publisher.Publish("s1", evt);

        // live 流仅在 Dispose 后收口（生产语义：消费方完成枚举后注销）；
        // 已入队事件在收口后仍可读出
        subscription.Dispose();

        var received = await ReadAllAsync(subscription);

        received.Should().BeEquivalentTo(Events, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task SubscribeLive_ShouldNotReplayBufferedHistory()
    {
        var publisher = CreatePublisher();

        // 订阅前发布（含上一轮完整事件序列）→ 只进缓冲，不进实时订阅
        foreach (var evt in Events)
            publisher.Publish("s1", evt);

        using var subscription = publisher.SubscribeLive("s1");

        // 无新事件 → 收口后读到空（不回放缓冲历史）
        subscription.Dispose();

        var received = await ReadAllAsync(subscription);

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscribeLive_Dispose_ShouldStopReceivingAndUnregister()
    {
        var publisher = CreatePublisher();

        var subscription = publisher.SubscribeLive("s1");
        publisher.Publish("s1", Events[0]);
        subscription.Dispose();

        publisher.Publish("s1", Events[1]);

        var received = await ReadAllAsync(subscription);
        received.Should().HaveCount(1);
        // 重复 Dispose 安全
        subscription.Dispose();

        publisher.SubscriberEntryCount.Should().Be(0);
    }

    [Fact]
    public async Task SubscribeLive_InvalidSessionId_ShouldReturnCompletedSubscription()
    {
        var publisher = CreatePublisher();

        var subscription = publisher.SubscribeLive("");

        // 已收口的空订阅：枚举立即结束、无事件
        var received = await ReadAllAsync(subscription);
        received.Should().BeEmpty();
        publisher.SubscriberEntryCount.Should().Be(0);
    }

    private static ExecutionEventPublisher CreatePublisher() =>
        new(new ExecutionOptions(), NullLogger<ExecutionEventPublisher>.Instance);

    private static async Task<List<IMessageEvent>> ReadAllAsync(ILiveEventSubscription subscription)
    {
        var received = new List<IMessageEvent>();
        await foreach (var evt in subscription)
            received.Add(evt);
        return received;
    }
}
