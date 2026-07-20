using FluentAssertions;
using Seeing.Agent.Core.Reminders;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Core.Reminders;

public class SystemReminderRendererTests
{
    [Fact]
    public void Wrap_Then_TryParse_RoundTrips_Task_Source_Kind_TaskId()
    {
        var wrapped = SystemReminderRenderer.Wrap(
            "每小时检查一次构建状态并汇总。",
            SystemReminder.Sources.Job,
            SystemReminder.Kinds.Cron);

        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Source.Should().Be("job");
        parts.Kind.Should().Be("cron");
        parts.Task.Should().Be("每小时检查一次构建状态并汇总。");
        parts.TaskId.Should().BeNull();
        parts.Notice.Should().NotBeNullOrWhiteSpace();
        parts.Raw.Should().Be(wrapped);
        wrapped.Should().Contain("<notice>");
        wrapped.Should().Contain("<task>");
        wrapped.Should().NotContain("每小时检查一次构建状态并汇总。\n</notice>");
    }

    [Fact]
    public void Wrap_WithTaskId_PreservesAttribute()
    {
        var wrapped = SystemReminderRenderer.Wrap(
            "done",
            SystemReminder.Sources.Task,
            SystemReminder.Kinds.Completed,
            taskId: "abc123");

        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.TaskId.Should().Be("abc123");
        wrapped.Should().Contain("task-id=\"abc123\"");
    }

    [Fact]
    public void Wrap_Escapes_ClosingSlash_In_TaskBody()
    {
        var body = "see </task> and </system-reminder> inside";
        var wrapped = SystemReminderRenderer.Wrap(body, "job", "cron");
        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Task.Should().Be(body);
        // 信封内原始序列含转义，避免提前闭合
        wrapped.Should().Contain("<\\/task>");
    }

    [Fact]
    public void Wrap_EmptyTaskBody_StillSucceeds()
    {
        var wrapped = SystemReminderRenderer.Wrap("", "job", "cron");
        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Task.Should().BeEmpty();
    }

    [Fact]
    public void Wrap_Throws_When_Source_Or_Kind_Blank()
    {
        var act1 = () => SystemReminderRenderer.Wrap("x", "", "cron");
        var act2 = () => SystemReminderRenderer.Wrap("x", "job", "  ");
        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryParse_ReturnsFalse_For_PlainUserText()
    {
        SystemReminderRenderer.TryParse("hello user", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateUserMessage_Sets_Content_And_Metadata()
    {
        var msg = SystemReminderRenderer.CreateUserMessage(
            "prompt", SystemReminder.Sources.Job, SystemReminder.Kinds.Heartbeat);

        msg.Role.Should().Be(MessageRole.User);
        SystemReminderRenderer.TryParse(msg.Content, out _).Should().BeTrue();
        msg.Metadata.Should().ContainKey(SystemReminder.MetadataKeys.Reminder);
        msg.Metadata![SystemReminder.MetadataKeys.Source].Should().Be("job");
        msg.Metadata[SystemReminder.MetadataKeys.Kind].Should().Be("heartbeat");
    }

    [Fact]
    public void Notices_UnknownKind_Uses_Generic()
    {
        var wrapped = SystemReminderRenderer.Wrap("x", "custom", "weird");
        SystemReminderRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Notice.Should().Contain("系统提醒");
    }
}
