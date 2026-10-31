using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Tools;

/// <summary>
/// schedule 入参形态容忍度：模型经常传字符串化 JSON、裸 cron、snake_case 或空格分隔时间，
/// 这些形态都必须被接受，否则会表现为“连官方示例都解析失败”。
/// </summary>
public class CronScheduleParserToleranceTests
{
    private static JsonElement Args(string rawJson) => JsonDocument.Parse(rawJson).RootElement.Clone();

    private static Mock<IScheduleManager> Manager()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);
        return manager;
    }

    private static async Task<(ToolResult Result, ScheduledJobSpec? Job)> RunAsync(string rawJson)
    {
        ScheduledJobSpec? captured = null;
        var manager = Manager();
        manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJobSpec, CancellationToken>((job, _) => captured = job)
            .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);

        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(Args(rawJson), new ToolContext { SessionId = "s" });
        return (result, captured);
    }

    [Fact]
    public async Task StandardObjectCron_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":{"type":"cron","cron":"0 12 * * *","timezone":"Asia/Shanghai"}}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Type.Should().Be(ScheduleTypes.Cron);
        job.Schedule.Cron.Should().Be("0 12 * * *");
    }

    [Fact]
    public async Task ScheduleAsJsonString_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":"{\"type\":\"cron\",\"cron\":\"0 12 * * *\"}"}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Cron.Should().Be("0 12 * * *");
    }

    [Fact]
    public async Task ScheduleAsBareCronString_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":"0 12 * * *"}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Type.Should().Be(ScheduleTypes.Cron);
        job.Schedule.Cron.Should().Be("0 12 * * *");
    }

    [Fact]
    public async Task ScheduleAsIntervalString_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":"6h"}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Type.Should().Be(ScheduleTypes.Interval);
        job.Schedule.Every.Should().Be("6h");
    }

    [Fact]
    public async Task ScheduleAsBareDateTimeString_ShouldSucceedAsOnce()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":"2026-12-31 12:00:00"}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Type.Should().Be(ScheduleTypes.Once);
        job.Schedule.RunAt.Should().Be(new DateTime(2026, 12, 31, 12, 0, 0));
    }

    [Fact]
    public async Task ScheduleAsDateOnlyString_ShouldSucceedAsOnce()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":"2026-12-31"}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Type.Should().Be(ScheduleTypes.Once);
        job.Schedule.RunAt.Should().Be(new DateTime(2026, 12, 31));
    }

    [Fact]
    public async Task OnceWithSpaceSeparatedDateTime_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":{"type":"once","runAt":"2026-12-31 12:00:00"}}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.RunAt.Should().Be(new DateTime(2026, 12, 31, 12, 0, 0));
    }

    [Fact]
    public async Task SnakeCaseRunAt_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":{"type":"once","run_at":"2026-12-31T12:00:00"}}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.RunAt.Should().Be(new DateTime(2026, 12, 31, 12, 0, 0));
    }

    [Fact]
    public async Task CronExpressionAlias_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":{"expression":"0 12 * * *"}}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Type.Should().Be(ScheduleTypes.Cron);
        job.Schedule.Cron.Should().Be("0 12 * * *");
    }

    [Fact]
    public async Task MissingScheduleButTopLevelCron_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","cron":"0 12 * * *"}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Cron.Should().Be("0 12 * * *");
    }

    [Fact]
    public async Task WindowsAsStringRange_ShouldSucceed()
    {
        var (result, job) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":{"type":"interval","every":"40m","windows":["09:00-23:00"]}}
            """);

        result.Success.Should().BeTrue(result.Error);
        job!.Schedule.Windows.Should().HaveCount(1);
        job.Schedule.Windows![0].Start.Should().Be("09:00");
        job.Schedule.Windows[0].End.Should().Be("23:00");
    }

    [Fact]
    public async Task UnparsableRunAt_ShouldFailWithActionableMessage()
    {
        var (result, _) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":{"type":"once","runAt":"明天中午"}}
            """);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("runAt");
        result.Error.Should().Contain("yyyy-MM-dd");
    }

    [Fact]
    public async Task UnparsableString_ShouldFailInsteadOfThrowing()
    {
        var (result, _) = await RunAsync("""
            {"taskType":"text","prompt":"提醒","schedule":"{not-json"}
            """);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("示例");
    }
}
