using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>串行闸：并发进入时互斥、异常后可继续使用、可重入。</summary>
public class SerialGateTests
{
    [Fact]
    public void Enter_ShouldSerializeConcurrentWriters()
    {
        var gate = new SerialGate();
        var concurrent = 0;
        var maxConcurrent = 0;
        var completed = 0;

        Parallel.For(0, 200, _ =>
        {
            using (gate.Enter())
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                Thread.SpinWait(2000);
                Interlocked.Decrement(ref concurrent);
                Interlocked.Increment(ref completed);
            }
        });

        completed.Should().Be(200);
        maxConcurrent.Should().Be(1);
    }

    [Fact]
    public void Enter_AfterException_ShouldStillBeUsable()
    {
        var gate = new SerialGate();

        var act = () => { using (gate.Enter()) throw new InvalidOperationException("模拟异常"); };
        act.Should().Throw<InvalidOperationException>();

        using var scope = gate.Enter();
        scope.Should().NotBeNull();
    }

    [Fact]
    public void Enter_NestedOnSameThread_ShouldBeReentrant()
    {
        // Monitor 可重入（SemaphoreSlim 不可），聚合器内可能存在嵌套调用同一临界区的路径
        var gate = new SerialGate();

        using (gate.Enter())
        {
            var act = () => { using (gate.Enter()) { } };
            act.Should().NotThrow();
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
