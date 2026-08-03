using Seeing.Agent.Core.Reminders;
using Xunit;

namespace Seeing.Agent.Tests.Core.Reminders;

public class SystemReminderNoticesTests
{
    [Fact]
    public void Resolve_Agent_TodoEmpty()
    {
        var notice = SystemReminderNotices.Resolve(
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoEmpty);
        Assert.Contains("TodoWrite", notice);
    }

    [Fact]
    public void Resolve_Agent_TodoIncomplete()
    {
        var notice = SystemReminderNotices.Resolve(
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoIncomplete);
        Assert.Contains("todo", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Agent_TodoPaused()
    {
        var notice = SystemReminderNotices.Resolve(
            SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoPaused);
        Assert.Contains("暂停", notice);
    }

    [Fact]
    public void Resolve_Unknown_FallsBack()
    {
        var notice = SystemReminderNotices.Resolve("unknown", "unknown");
        Assert.NotEmpty(notice);
    }
}
