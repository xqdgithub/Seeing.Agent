using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>interval 起算锚点只影响显式带 lastRunAt 的任务（心跳）；用户任务保持原有 5 秒行为。</summary>
public class HeartbeatTriggerAnchorTests
{
    [Fact]
    public async Task IntervalWithAnchor_StartsAtLastRunPlusInterval()
    {
        var engine = await CreateEngineAsync();
        var last = DateTime.Now.AddHours(-1);

        await engine.UpsertJobAsync(
            "_heartbeat",
            new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h", Timezone = TimeZoneInfo.Local.Id },
            ScheduleIntent.Active,
            new JobDataMap { [JobDataKeys.LastRunAt] = last.ToString("O") },
            TestContext.Current.CancellationToken);

        var next = await FirstNextFireAsync(engine);
        next.Should().BeCloseTo(last.AddHours(6), TimeSpan.FromMinutes(1),
            "重启后必须按「上次执行 + 间隔」续上，而不是重新计时");
    }

    [Fact]
    public async Task IntervalWithoutAnchor_StaysAtFiveSeconds()
    {
        var engine = await CreateEngineAsync();

        await engine.UpsertJobAsync(
            "job_x",
            new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h", Timezone = TimeZoneInfo.Local.Id },
            ScheduleIntent.Active,
            new JobDataMap(),
            TestContext.Current.CancellationToken);

        var next = await FirstNextFireAsync(engine);
        next.Should().BeCloseTo(DateTime.Now.AddSeconds(5), TimeSpan.FromSeconds(10),
            "用户 interval 任务的既有行为不能被心跳改动牵连");
    }

    [Fact]
    public async Task IntervalWithOverdueAnchor_DoesNotFireImmediately()
    {
        var engine = await CreateEngineAsync();
        var last = DateTime.Now.AddHours(-48);

        await engine.UpsertJobAsync(
            "_heartbeat",
            new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h", Timezone = TimeZoneInfo.Local.Id },
            ScheduleIntent.Active,
            new JobDataMap { [JobDataKeys.LastRunAt] = last.ToString("O") },
            TestContext.Current.CancellationToken);

        var next = await FirstNextFireAsync(engine);
        next.Should().BeAfter(DateTime.Now.AddMinutes(1),
            "逾期锚点只做对齐，不在启动瞬间补跑");
    }

    [Fact]
    public async Task RemoveJob_OnFreshEngine_InitializesSchedulerInsteadOfNoOp()
    {
        // 未 Start 的引擎：早期实现里 _scheduler == null 会直接 return，
        // 于是持久化存储里的旧心跳触发器删不掉，升级后第一次启动会先按旧表 misfire。
        var engine = await CreateEngineAsync(start: false);

        var act = () => engine.RemoveJobAsync("_heartbeat", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        engine.Scheduler.Should().NotBeNull("删除必须先拿到实例，而不是静默返回");
    }

    private static async Task<DateTime> FirstNextFireAsync(QuartzSchedulerEngine engine)
    {
        var scheduler = engine.Scheduler!;
        var keys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
        var key = keys.Single();
        var triggers = await scheduler.GetTriggersOfJob(key);
        return triggers.Select(t => t.GetNextFireTimeUtc())
            .Where(t => t.HasValue)
            .Select(t => t!.Value.LocalDateTime)
            .Min();
    }

    private static async Task<QuartzSchedulerEngine> CreateEngineAsync(bool start = true)
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "AnchorTest_" + Guid.NewGuid().ToString("N")[..8],
            ["quartz.threadPool.threadCount"] = "2"
        };
        var factory = new StdSchedulerFactory(props);
        var scheduler = await factory.GetScheduler();
        var engine = new QuartzSchedulerEngine(new FixedFactory(scheduler), NullLogger<QuartzSchedulerEngine>.Instance);
        if (start)
            await engine.StartAsync(TestContext.Current.CancellationToken);
        return engine;
    }

    private sealed class FixedFactory : ISchedulerFactory
    {
        private readonly IScheduler _scheduler;
        public FixedFactory(IScheduler scheduler) => _scheduler = scheduler;
        public Task<IScheduler> GetScheduler(CancellationToken ct = default) => Task.FromResult(_scheduler);
        public Task<IScheduler?> GetScheduler(string name, CancellationToken ct = default) => Task.FromResult<IScheduler?>(_scheduler);
        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IScheduler>>(new[] { _scheduler });
    }
}
