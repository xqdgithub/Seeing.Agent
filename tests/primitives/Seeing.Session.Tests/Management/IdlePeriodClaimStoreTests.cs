using FluentAssertions;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Session.Tests.Management;

/// <summary>
/// 空闲期占位登记：让「消费方去重」与「共享活动事实」解耦。
/// <para>
/// 背景：记忆演化默认 15 分钟就处理完空闲会话，复盘默认等 30 分钟。原先记忆侧用
/// <see cref="Core.ISessionActivityTracker.Clear"/> 做去重，等于把共享事实删掉——
/// 复盘的 <c>GetIdleSessions(30min)</c> 永远拿不到该会话，空闲触发<b>永久静默失效</b>。
/// 本类把去重收进各消费方自己手里，事实从此不可被删除。
/// </para>
/// </summary>
public sealed class IdlePeriodClaimStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsClaimed_Should_Be_False_Before_Claim()
    {
        new IdlePeriodClaimStore().IsClaimed("s1", T1).Should().BeFalse();
    }

    [Fact]
    public void Claim_Then_IsClaimed_Should_Be_True_For_Same_Period()
    {
        var store = new IdlePeriodClaimStore();
        store.Claim("s1", T1);

        store.IsClaimed("s1", T1).Should().BeTrue();
    }

    [Fact]
    public void New_Activity_Period_Should_Not_Be_Claimed()
    {
        // 用户回到会话后 Touch 会换一个新时刻：这是新的空闲期，必须允许再处理一次。
        var store = new IdlePeriodClaimStore();
        store.Claim("s1", T1);

        store.IsClaimed("s1", T1.AddMinutes(5)).Should().BeFalse(
            "活动时刻变了就是新的空闲期，不能被上一段的登记挡住");
    }

    [Fact]
    public void Release_Should_Allow_Retry()
    {
        // 队列满等「占了位但最终没处理」的路径必须能撤销，否则该会话被永久跳过。
        var store = new IdlePeriodClaimStore();
        store.Claim("s1", T1);
        store.Release("s1");

        store.IsClaimed("s1", T1).Should().BeFalse();
        store.Count.Should().Be(0);
    }

    [Fact]
    public void Blank_SessionId_Should_Be_Ignored()
    {
        var store = new IdlePeriodClaimStore();
        store.Claim("", T1);
        store.Claim("   ", T1);

        store.Count.Should().Be(0);
        store.IsClaimed("", T1).Should().BeFalse();
    }

    [Fact]
    public void Prune_Should_Drop_Entries_Whose_Activity_Changed_Or_Session_Vanished()
    {
        var store = new IdlePeriodClaimStore();
        store.Claim("kept", T1);
        store.Claim("reactivated", T1);
        store.Claim("deleted", T1);

        // kept：时刻不变；reactivated：用户回来了（时刻变了）；deleted：追踪器里没有了。
        store.Prune(id => id switch
        {
            "kept" => T1,
            "reactivated" => T1.AddMinutes(3),
            _ => null
        });

        store.Count.Should().Be(1);
        store.IsClaimed("kept", T1).Should().BeTrue();
    }

    /// <summary>
    /// 关键回归：两个消费方各持一份登记时，一个消费方处理完不得影响另一个还能看到该空闲期。
    /// 这正是原先 <c>Clear</c> 破坏的性质。
    /// </summary>
    [Fact]
    public void Two_Consumers_Should_Not_Starve_Each_Other()
    {
        var memory = new IdlePeriodClaimStore();
        var reflection = new IdlePeriodClaimStore();

        memory.Claim("s1", T1);

        memory.IsClaimed("s1", T1).Should().BeTrue();
        reflection.IsClaimed("s1", T1).Should().BeFalse(
            "记忆演化处理过不等于复盘也处理过；共享去重状态会让后处理者永久被挡住");
    }
}
