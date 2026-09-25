using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Execution;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// 执行事件发布者的发布/订阅临界区语义回归：
/// 订阅者在订阅建立瞬间的缓冲区回放与实时扇出之间必须「恰好一次」，不丢不重。
/// </summary>
public class ExecutionEventPublisherTests
{
    private static ExecutionEventPublisher CreatePublisher(int bufferSize = 100000)
        => new(new ExecutionOptions { EventBufferSize = bufferSize },
            NullLogger<ExecutionEventPublisher>.Instance);

    private static StreamDeltaEvent Delta(int sequence, string sessionId = "s1")
        => new() { SessionId = sessionId, ContentDelta = sequence.ToString() };

    private static int SequenceOf(IMessageEvent evt)
        => int.Parse(((StreamDeltaEvent)evt).ContentDelta!);

    [Fact]
    public async Task SubscribeAsync_ShouldReplayBufferedEventsThenLiveEvents_ExactlyOnce()
    {
        using var publisher = CreatePublisher();
        publisher.Publish("s1", Delta(0));
        publisher.Publish("s1", Delta(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<int>();
        await using var enumerator = publisher.SubscribeAsync("s1", cts.Token)
            .GetAsyncEnumerator(cts.Token);

        // 订阅建立即回放订阅前已缓冲的历史事件（顺序 0、1）
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        received.Add(SequenceOf(enumerator.Current));
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        received.Add(SequenceOf(enumerator.Current));

        // 回放消费完毕后订阅已建立；此后发布走实时扇出
        publisher.Publish("s1", Delta(2));
        publisher.Publish("s1", Delta(3));

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        received.Add(SequenceOf(enumerator.Current));
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        received.Add(SequenceOf(enumerator.Current));

        received.Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public async Task SubscribeAsync_DuringConcurrentPublish_ShouldDeliverEachEventExactlyOnce()
    {
        const int total = 3000;
        const int rounds = 24;

        for (var round = 0; round < rounds; round++)
        {
            using var publisher = CreatePublisher(bufferSize: 100000);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 发布进程中线程周期性让出，制造订阅注册/回放与发布扇出交错
            var producer = Task.Run(() =>
            {
                for (var i = 0; i < total; i++)
                {
                    publisher.Publish("s1", Delta(i));
                    if ((i & 7) == 0)
                        Thread.Yield();
                }
            }, cts.Token);

            var received = new List<int>(total);
            await foreach (var evt in publisher.SubscribeAsync("s1", cts.Token))
            {
                received.Add(SequenceOf(evt));
                if (received[^1] == total - 1)
                    break;
            }

            await producer;

            received.Should().Equal(Enumerable.Range(0, total),
                $"第 {round} 轮并发发布/订阅不得丢事件或重复投递");
        }
    }

    [Fact]
    public async Task CompleteSession_ShouldCompleteSubscribersAndClearBuffer()
    {
        using var publisher = CreatePublisher();
        publisher.Publish("s1", Delta(0));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = publisher.SubscribeAsync("s1", cts.Token)
            .GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        SequenceOf(enumerator.Current).Should().Be(0);

        publisher.CompleteSession("s1");

        // 订阅流应随 CompleteSession 结束
        (await enumerator.MoveNextAsync()).Should().BeFalse();
        publisher.GetBufferedEvents("s1").Should().BeEmpty();
    }

    [Fact]
    public void ClearBuffer_ShouldRemoveBufferedEvents()
    {
        using var publisher = CreatePublisher();
        publisher.Publish("s1", Delta(0));
        publisher.GetBufferedEvents("s1").Should().ContainSingle();

        publisher.ClearBuffer("s1");
        publisher.GetBufferedEvents("s1").Should().BeEmpty();
    }

    [Fact]
    public void Publish_WithInvalidArguments_ShouldBeNoOp()
    {
        using var publisher = CreatePublisher();
        publisher.Publish("", Delta(0));
        publisher.Publish("s1", null!);

        publisher.GetBufferedEvents("s1").Should().BeEmpty();
    }
}
