using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>
/// 轮次计数器的有界性。
/// <para>
/// 计数器为每个观察到终态的会话建条目，而会话删除路径在 primitives 层、
/// 不该反向依赖本包，因此没有天然的清理时机。不设上限就会随进程生命周期
/// 内的会话总数无界增长。
/// </para>
/// </summary>
public sealed class ReflectionTurnCounterTests
{
    [Fact]
    public void Increment_Should_Accumulate()
    {
        var counter = new ReflectionTurnCounter();

        counter.Increment("s1").Should().Be(1);
        counter.Increment("s1").Should().Be(2);
        counter.GetCount("s1").Should().Be(2);
    }

    [Fact]
    public void Increment_Should_Ignore_Blank_SessionId()
    {
        var counter = new ReflectionTurnCounter();

        counter.Increment("").Should().Be(0);
        counter.Increment("   ").Should().Be(0);
        counter.Count.Should().Be(0);
    }

    [Fact]
    public void Reset_Should_Zero_But_Keep_Key()
    {
        var counter = new ReflectionTurnCounter();
        counter.Increment("s1");
        counter.Increment("s1");

        counter.Reset("s1");

        counter.GetCount("s1").Should().Be(0);
        counter.Increment("s1").Should().Be(1, "Reset 后重新累计");
    }

    [Fact]
    public void Clear_Should_Remove_Entry()
    {
        var counter = new ReflectionTurnCounter();
        counter.Increment("s1");

        counter.Clear("s1");

        counter.Count.Should().Be(0);
        counter.GetCount("s1").Should().Be(0);
    }

    [Fact]
    public void Count_Should_Not_Exceed_Capacity()
    {
        // 核心守门：大量会话进出后，条目数必须被压在上限以内。
        const int capacity = 16;
        var counter = new ReflectionTurnCounter(capacity);

        for (var i = 0; i < 500; i++)
        {
            counter.Increment("session-" + i);
        }

        counter.Count.Should().BeLessThanOrEqualTo(capacity,
            "条目数超限时必须淘汰，否则字典随会话数无界增长");
    }

    [Fact]
    public void Eviction_Should_Drop_Least_Recently_Used()
    {
        const int capacity = 4;
        var counter = new ReflectionTurnCounter(capacity);

        // 先塞满并让 oldest 明确成为最久未触发的那一个。
        counter.Increment("oldest");
        Thread.Sleep(20);
        for (var i = 0; i < capacity - 1; i++)
            counter.Increment("filler-" + i);

        // 再触发一个，挤掉 oldest。
        counter.Increment("newcomer");

        counter.GetCount("oldest").Should().Be(0,
            "最久未触发的会话应被优先淘汰");
        counter.GetCount("newcomer").Should().Be(1);
    }

    [Fact]
    public void Recently_Touched_Entry_Should_Survive_Eviction()
    {
        const int capacity = 3;
        var counter = new ReflectionTurnCounter(capacity);

        // 先塞满，让所有条目都老。
        for (var i = 0; i < capacity; i++)
            counter.Increment("filler-" + i);

        // 再触发一个旧会话，它成为最新。
        counter.Increment("keep-me");

        // 挤掉一个：应淘汰最旧的 filler，而不是刚触发过的 keep-me（LRU 而非 FIFO）。
        counter.Increment("newcomer");

        counter.GetCount("keep-me").Should().Be(1,
            "刚被触发过的会话是最新的，不该被优先淘汰");
        counter.GetCount("newcomer").Should().Be(1);
    }

    [Fact]
    public void Constructor_Should_Reject_Non_Positive_Capacity()
    {
        var act = () => new ReflectionTurnCounter(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
