using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class JsonScheduleRepositoryTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsJobs()
    {
        using var ws = new SchedulerTestWorkspace();
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);

        var jobs = new JobsFile
        {
            Jobs =
            [
                new ScheduledJobSpec
                {
                    Id = "job-1",
                    Name = "daily-check",
                    TaskType = ScheduleTaskTypes.Agent,
                    Agent = "test-agent",
                    Prompt = "检查待办",
                    Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 9 * * *" },
                    Dispatch = new DispatchSpec
                    {
                        Target = new DispatchTarget { SessionId = "main" }
                    }
                }
            ]
        };

        await repo.SaveAsync(jobs, TestContext.Current.CancellationToken);
        var loaded = await repo.LoadAsync(TestContext.Current.CancellationToken);

        loaded.Jobs.Should().HaveCount(1);
        loaded.Jobs[0].Id.Should().Be("job-1");
        loaded.Jobs[0].Prompt.Should().Be("检查待办");
        loaded.Jobs[0].Schedule.Cron.Should().Be("0 9 * * *");

        var jobsPath = Path.Combine(ws.SeeingDirectory, "jobs.json");
        File.Exists(jobsPath).Should().BeTrue();
    }

    [Fact]
    public async Task AppendHistory_RespectsMaxRecords()
    {
        using var ws = new SchedulerTestWorkspace();
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);

        for (var i = 0; i < SchedulerConstants.MaxHistoryRecords + 5; i++)
        {
            await repo.AppendHistoryAsync("job-1", new JobExecutionRecord
            {
                RunId = $"run-{i}",
                Status = "success",
                Output = $"output-{i}"
            }, TestContext.Current.CancellationToken);
        }

        var history = await repo.GetHistoryAsync("job-1", 100, ct: TestContext.Current.CancellationToken);
        history.Should().HaveCount(SchedulerConstants.MaxHistoryRecords);
        history[0].RunId.Should().Be($"run-{SchedulerConstants.MaxHistoryRecords + 4}");
    }

    /// <summary>
    /// 两个仓储实例并发追加同一任务的历史时，任何一次写入都不得抛异常。
    /// <para>
    /// 原 bug：临时文件名固定为 <c>{path}.tmp</c>，而互斥锁 <c>SemaphoreSlim</c> 是**实例级**的。
    /// 两个实例（冷/热实例、宿主与 CLI、测试与宿主）各持一把独立的锁，
    /// 于是互相之间没有任何互斥：A 写完 tmp，B 用同名 tmp 覆盖，
    /// A 去 move 时文件已被 B 移走 → <see cref="FileNotFoundException"/>
    /// （<c>File.Move</c> 找不到源文件）。重试也救不了：tmp 已被消费，重试仍是找不到。
    /// </para>
    /// <para>
    /// 这条用例即现场日志复现的那条路径 —— 任务实际执行成功，却因为追加历史失败
    /// 被 <c>AgentJob</c> 的 catch 记成「execution failed」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AppendHistory_TwoInstancesConcurrently_ShouldNotThrow()
    {
        using var ws = new SchedulerTestWorkspace();

        // 两个独立实例 = 两把独立的实例级锁（这正是生产中的形态：DI 单例 + 测试/CLI 另建）
        var repoA = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var repoB = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);

        var ct = TestContext.Current.CancellationToken;

        // 总数必须低于 SchedulerConstants.MaxHistoryRecords（50），
        // 否则触发上限截断，就分不清「记录丢失」和「正常裁剪」了
        const int perInstance = 20;

        // 交错发起，最大化撞车概率
        var tasks = new List<Task>();
        for (var i = 0; i < perInstance; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() => repoA.AppendHistoryAsync("job-shared", Record($"a-{index}"), ct), ct));
            tasks.Add(Task.Run(() => repoB.AppendHistoryAsync("job-shared", Record($"b-{index}"), ct), ct));
        }

        var act = async () => await Task.WhenAll(tasks);

        await act.Should().NotThrowAsync(
            "两个实例必须能并发写同一份历史；固定临时文件名会互相消费导致 FileNotFoundException");

        var history = await repoA.GetHistoryAsync("job-shared", perInstance * 2, ct: ct);

        // 不丢记录：每次追加都必须落到最终文件里（并发写不要求顺序，但要求一条不丢）
        history.Should().HaveCount(perInstance * 2,
            "并发追加不允许丢记录；锁与临时文件名都必须跨实例生效");
        history.Select(r => r.RunId).Distinct().Should().HaveCount(perInstance * 2);
    }

    /// <summary>
    /// 临时文件名必须唯一：固定 <c>{path}.tmp</c> 会让并发写入者互相消费。
    /// <para>
    /// 这条断言直接盯住原 bug 的形态。它不看行为（行为在上面那条并发用例里），
    /// 只看命名策略，因为「用固定名 + 重试」看起来很像能工作，
    /// 实际上重试永远等不到一个已被别人 move 掉的文件。
    /// </para>
    /// </summary>
    [Fact]
    public void TempPath_ShouldBeUniquePerWrite()
    {
        var destination = Path.Combine(Path.GetTempPath(), "seeing-demo", "job_1.json");

        var first = ScheduleAtomicFile.CreateTempPath(destination);
        var second = ScheduleAtomicFile.CreateTempPath(destination);

        first.Should().NotBe(second,
            "两方并发写同一文件时，固定临时名会互相覆盖/互相移动");
        first.Should().NotBe(destination + ".tmp",
            "不得再退回固定 .tmp 命名");
        Path.GetDirectoryName(first).Should().Be(Path.GetDirectoryName(destination),
            "临时文件必须与被替换文件同目录，否则 File.Move 会跨卷失败");
        first.Should().EndWith(".tmp");
    }

    /// <summary>
    /// 路径锁必须跨实例共享：同一路径两次取锁应拿到同一把（这是跨实例互斥的前提）。
    /// </summary>
    [Fact]
    public void PathLock_ShouldBeSharedAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "seeing-demo", "job_2.json");

        var a = ScheduleAtomicFile.GetLock(path);
        var b = ScheduleAtomicFile.GetLock(path);

        a.Should().BeSameAs(b, "锁挂在实例上就无法跨实例互斥——这正是原 bug 的一半");
    }

    private static JobExecutionRecord Record(string runId) => new()
    {
        RunId = runId,
        Status = "success",
        Output = runId
    };
}
