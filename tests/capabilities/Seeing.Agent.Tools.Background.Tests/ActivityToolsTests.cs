using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tools.Background.Tests;

public class ActivityToolsTests
{
    private const string OwnerSession = "session-owner";
    private const string OtherSession = "session-other";

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(string sessionId) =>
        new() { SessionId = sessionId, CancellationToken = TestContext.Current.CancellationToken };

    private static ActivityStatusTool StatusTool(FakeBackgroundActivityManager fake) =>
        new(NullLogger<ActivityStatusTool>.Instance, fake);

    private static ActivityOutputTool OutputTool(FakeBackgroundActivityManager fake) =>
        new(NullLogger<ActivityOutputTool>.Instance, fake);

    private static ActivityWaitTool WaitTool(FakeBackgroundActivityManager fake) =>
        new(NullLogger<ActivityWaitTool>.Instance, fake);

    private static ActivityCancelTool CancelTool(FakeBackgroundActivityManager fake) =>
        new(NullLogger<ActivityCancelTool>.Instance, fake);

    private static ActivityListTool ListTool(FakeBackgroundActivityManager fake) =>
        new(NullLogger<ActivityListTool>.Instance, fake);

    [Fact]
    public async Task Status_ShouldPassRequesterSessionId()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_1", OwnerSession));
        var tool = StatusTool(fake);

        var result = await tool.ExecuteAsync(
            Args(new { activity_id = "act_1" }), Context(OwnerSession));

        result.Success.Should().BeTrue();
        fake.LastRequesterSessionId.Should().Be(OwnerSession);
        result.Output.Should().Contain("act_1").And.Contain("Running");
    }

    [Fact]
    public async Task Status_WithForeignSession_ShouldReturnNullMessage()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_1", OwnerSession));
        var tool = StatusTool(fake);

        var result = await tool.ExecuteAsync(
            Args(new { activity_id = "act_1" }), Context(OtherSession));

        result.Success.Should().BeTrue();
        fake.LastRequesterSessionId.Should().Be(OtherSession);
        result.Output.Should().Contain("未找到").And.Contain("不属于本会话");
        fake.ObservedActivityIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Status_OnTerminal_ShouldMarkResultObserved()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot(
            "act_done", OwnerSession, BackgroundActivityStatus.Succeeded));
        var tool = StatusTool(fake);

        var result = await tool.ExecuteAsync(
            Args(new { activity_id = "act_done" }), Context(OwnerSession));

        result.Success.Should().BeTrue();
        fake.ObservedActivityIds.Should().Equal("act_done");
    }

    [Fact]
    public async Task Output_ShouldDefaultTo32768()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_1", OwnerSession), output: "build output line");
        var tool = OutputTool(fake);

        var result = await tool.ExecuteAsync(
            Args(new { activity_id = "act_1" }), Context(OwnerSession));

        result.Success.Should().BeTrue();
        fake.LastRequesterSessionId.Should().Be(OwnerSession);
        fake.LastOffset.Should().Be(0);
        fake.LastMaxBytes.Should().Be(32768);
        result.Output.Should().Contain("build output line");
    }

    [Fact]
    public async Task Output_ShouldPageWithOffset()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_1", OwnerSession), output: "0123456789");
        var tool = OutputTool(fake);

        var result = await tool.ExecuteAsync(
            Args(new { activity_id = "act_1", offset = 4, max_bytes = 3 }), Context(OwnerSession));

        result.Success.Should().BeTrue();
        fake.LastOffset.Should().Be(4);
        fake.LastMaxBytes.Should().Be(3);
        result.Output.Should().StartWith("456");
        result.Output.Should().Contain("offset=7");
    }

    [Fact]
    public async Task Wait_ShouldReturnCallerCancelled()
    {
        var fake = new FakeBackgroundActivityManager
        {
            WaitOutcome = BackgroundActivityWaitOutcome.CallerCancelled,
        };
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_1", OwnerSession));
        var tool = WaitTool(fake);

        var result = await tool.ExecuteAsync(
            Args(new { activity_id = "act_1", timeout_ms = 5000 }), Context(OwnerSession));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("中断");
        result.Output.Should().Contain("继续运行");
        fake.LastTimeout.Should().Be(TimeSpan.FromMilliseconds(5000));
        fake.Find("act_1")!.Status.Should().Be(BackgroundActivityStatus.Running);
        fake.ObservedActivityIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Wait_ShouldUseTenMinuteDefault()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_1", OwnerSession));
        var tool = WaitTool(fake);

        await tool.ExecuteAsync(Args(new { activity_id = "act_1" }), Context(OwnerSession));

        fake.LastTimeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Cancel_ShouldNotAffectOtherSession()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_1", OwnerSession));
        var tool = CancelTool(fake);

        var denied = await tool.ExecuteAsync(
            Args(new { activity_id = "act_1" }), Context(OtherSession));

        denied.Success.Should().BeFalse();
        fake.LastRequesterSessionId.Should().Be(OtherSession);
        fake.Find("act_1")!.Status.Should().Be(BackgroundActivityStatus.Running);

        var allowed = await tool.ExecuteAsync(
            Args(new { activity_id = "act_1" }), Context(OwnerSession));

        allowed.Success.Should().BeTrue();
        fake.Find("act_1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        fake.Find("act_1")!.CancelReason.Should().Be(BackgroundActivityCancelReason.User);
    }

    [Fact]
    public async Task List_ShouldOnlyReturnOwnSession()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_mine", OwnerSession));
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_foreign", OtherSession));
        var tool = ListTool(fake);

        var result = await tool.ExecuteAsync(Args(new { }), Context(OwnerSession));

        result.Success.Should().BeTrue();
        fake.LastRequesterSessionId.Should().Be(OwnerSession);
        result.Output.Should().Contain("act_mine");
        result.Output.Should().NotContain("act_foreign");
    }

    [Fact]
    public async Task List_ShouldRejectUnknownStatus()
    {
        var fake = new FakeBackgroundActivityManager();
        var tool = ListTool(fake);

        var result = await tool.ExecuteAsync(Args(new { status = "Bogus" }), Context(OwnerSession));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未知的 status");
    }

    [Fact]
    public async Task List_ShouldFilterByStatusAndCompleted()
    {
        var fake = new FakeBackgroundActivityManager();
        fake.Add(FakeBackgroundActivityManager.Snapshot("act_running", OwnerSession));
        fake.Add(FakeBackgroundActivityManager.Snapshot(
            "act_done", OwnerSession, BackgroundActivityStatus.Succeeded));
        var tool = ListTool(fake);

        var running = await tool.ExecuteAsync(
            Args(new { status = "running" }), Context(OwnerSession));
        running.Output.Should().Contain("act_running").And.NotContain("act_done");

        var activeOnly = await tool.ExecuteAsync(
            Args(new { include_completed = false }), Context(OwnerSession));
        activeOnly.Output.Should().Contain("act_running").And.NotContain("act_done");
    }
}
