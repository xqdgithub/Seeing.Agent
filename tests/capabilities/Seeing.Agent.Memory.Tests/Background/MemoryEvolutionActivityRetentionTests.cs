using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Background;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Background;

/// <summary>
/// 记忆演化不得删除共享的活动事实。
/// <para>
/// <b>这是一条真实发生过的功能级故障</b>：演化默认 15 分钟处理一次空闲会话，
/// 处理完调用 <c>ISessionActivityTracker.Clear</c> 把时间戳删掉；而复盘的默认空闲阈值是 30 分钟。
/// 于是复盘的 <c>GetIdleSessions(30min)</c> 永远拿不到任何会话——
/// 空闲触发<b>永久失效</b>，且没有任何报错、日志或指标能看出来。
/// </para>
/// <para>
/// 正确分工：追踪器里的时间戳是<b>全进程共享的事实</b>，只由主执行链 <c>Touch</c>、
/// 只在会话被真正删除时 <c>Clear</c>；消费方的「处理过了」去重必须各自持有
/// （<see cref="Seeing.Session.Management.IdlePeriodClaimStore"/>）。
/// </para>
/// </summary>
public sealed class MemoryEvolutionActivityRetentionTests
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Evolution_Should_Not_Clear_Shared_Activity_Timestamp()
    {
        var tracker = new Seeing.Session.Management.SessionActivityTracker();
        tracker.Touch("ses-1");

        // 让会话满足「空闲 ≥ 1 分钟」：用真实时间不可行（不能 sleep），改用极小阈值。
        await RunOneIdleRoundAsync(tracker, TimeSpan.Zero);

        tracker.TryGetLastActivity("ses-1", out _).Should().BeTrue(
            "演化处理完不得删除共享事实，否则复盘的更长空闲窗口永远等不到这个会话");
    }

    [Fact]
    public async Task Evolution_Should_Only_Evolve_Once_Per_Idle_Period()
    {
        // 去重仍在：同一段空闲期内重复扫描不能反复演化（否则每次 tick 都烧一次模型调用）。
        var tracker = new Seeing.Session.Management.SessionActivityTracker();
        var claims = new Seeing.Session.Management.IdlePeriodClaimStore();
        tracker.Touch("ses-1");

        var evolution = new CountingEvolutionService();

        await SimulateIdleRound(tracker, claims, TimeSpan.Zero, evolution);
        await SimulateIdleRound(tracker, claims, TimeSpan.Zero, evolution);

        evolution.Count.Should().Be(1, "同一段空闲期只处理一次");
    }

    [Fact]
    public async Task New_Activity_Period_Should_Be_Evolved_Again()
    {
        var tracker = new Seeing.Session.Management.SessionActivityTracker();
        var claims = new Seeing.Session.Management.IdlePeriodClaimStore();
        tracker.Touch("ses-1");

        var evolution = new CountingEvolutionService();
        await SimulateIdleRound(tracker, claims, TimeSpan.Zero, evolution);

        // 用户回来了：Touch 换新时刻 → 新的空闲期 → 应允许再演化一次。
        tracker.Touch("ses-1");
        await SimulateIdleRound(tracker, claims, TimeSpan.Zero, evolution);

        evolution.Count.Should().Be(2, "活动时刻变了就是新的空闲期");
    }

    /// <summary>
    /// 跑一轮空闲循环的演化判定。
    /// <para>
    /// <c>MemoryEvolutionWorker</c> 的空闲循环是私有实现（含 <c>Task.Delay(1min)</c>），
    /// 无法直接驱动一轮；这里直接复刻同一套协作对象与判定顺序，
    /// 而不是把 worker 的内部时序重写一遍。
    /// </para>
    /// </summary>
    private static Task RunOneIdleRoundAsync(
        Seeing.Session.Management.SessionActivityTracker tracker,
        TimeSpan idleThreshold,
        CountingEvolutionService? evolution = null)
        => SimulateIdleRound(
            tracker,
            new Seeing.Session.Management.IdlePeriodClaimStore(),
            idleThreshold,
            evolution ?? new CountingEvolutionService());

    /// <summary>
    /// 复刻 <c>MemoryEvolutionWorker.IdleLoopAsync</c> 的空闲段（同样的门禁与去重顺序）。
    /// </summary>
    private static async Task SimulateIdleRound(
        Seeing.Session.Management.SessionActivityTracker tracker,
        Seeing.Session.Management.IdlePeriodClaimStore claims,
        TimeSpan idleThreshold,
        CountingEvolutionService evolution)
    {
        foreach (var sessionId in tracker.GetIdleSessions(idleThreshold))
        {
            if (!tracker.TryGetLastActivity(sessionId, out var lastActivity))
                continue;
            if (DateTimeOffset.UtcNow - lastActivity < idleThreshold)
                continue;
            if (claims.IsClaimed(sessionId, lastActivity))
                continue;

            await evolution.EvolveSessionAsync(sessionId, CancellationToken.None);
            claims.Claim(sessionId, lastActivity);
        }

        claims.Prune(id => tracker.TryGetLastActivity(id, out var last) ? last : null);
    }

    private sealed class CountingEvolutionService : IMemoryEvolutionService
    {
        public int Count { get; private set; }

        public Task EvolveSessionAsync(string sessionId, CancellationToken ct = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
