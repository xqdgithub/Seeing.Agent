using FluentAssertions;
using Seeing.Agent.TokenBudget.Api.Responses;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// TUI 预算通知器测试：状态栏按帧拉取快照，Hook 在后台线程发布，需线程安全 + 订阅回放。
/// </summary>
public sealed class TuiBudgetStatusNotifierTests
{
    private static BudgetStatusResponse Status(int current) => new() { SessionId = "ses_1", CurrentTokens = current };

    [Fact]
    public void Publish_ThenGetCurrent_ShouldReturnLatest()
    {
        var notifier = new TuiBudgetStatusNotifier();

        notifier.Publish("ses_1", Status(100));
        notifier.Publish("ses_1", Status(200));

        notifier.GetCurrentStatus("ses_1")!.CurrentTokens.Should().Be(200);
        notifier.GetCurrentStatus("ses_2").Should().BeNull();
    }

    [Fact]
    public void Publish_WithBlankSessionId_ShouldBeIgnored()
    {
        var notifier = new TuiBudgetStatusNotifier();

        notifier.Publish(string.Empty, Status(100));

        notifier.GetCurrentStatus(string.Empty).Should().BeNull();
    }

    [Fact]
    public void Subscribe_ShouldReplayCurrentSnapshot()
    {
        var notifier = new TuiBudgetStatusNotifier();
        notifier.Publish("ses_1", Status(100));

        var received = new List<int>();
        using var _ = notifier.Subscribe("ses_1", s => received.Add(s.CurrentTokens));

        received.Should().Equal(100);
    }

    [Fact]
    public void Subscribe_ThenPublish_ShouldNotify_AndDisposeShouldStop()
    {
        var notifier = new TuiBudgetStatusNotifier();
        var received = new List<int>();
        var subscription = notifier.Subscribe("ses_1", s => received.Add(s.CurrentTokens));

        notifier.Publish("ses_1", Status(100));
        subscription.Dispose();
        notifier.Publish("ses_1", Status(200));

        received.Should().Equal(100);
    }

    [Fact]
    public void Subscribe_WhenSubscriberThrows_ShouldNotBreakPublish()
    {
        var notifier = new TuiBudgetStatusNotifier();
        using var _ = notifier.Subscribe("ses_1", _ => throw new InvalidOperationException("boom"));

        var act = () => notifier.Publish("ses_1", Status(100));

        act.Should().NotThrow();
        notifier.GetCurrentStatus("ses_1")!.CurrentTokens.Should().Be(100);
    }

    [Fact]
    public void Publish_Concurrently_ShouldStayConsistent()
    {
        var notifier = new TuiBudgetStatusNotifier();

        Parallel.For(0, 200, i =>
        {
            notifier.Publish("ses_1", Status(i + 1));
            notifier.GetCurrentStatus("ses_1");
        });

        notifier.GetCurrentStatus("ses_1").Should().NotBeNull();
    }
}
