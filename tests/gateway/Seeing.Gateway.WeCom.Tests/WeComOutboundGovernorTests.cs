using FluentAssertions;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComOutboundGovernorTests
{
    [Fact]
    public async Task WaitForSlotAsync_ShouldBlockWhenOverLimit()
    {
        var governor = new WeComOutboundGovernor();
        var now = DateTime.UtcNow;

        for (var i = 0; i < WeComOutboundGovernor.MaxMessagesPerMinute; i++)
        {
            governor.RecordSend(WeComOutboundPriority.ContentDelta);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var act = () => governor.WaitForSlotAsync(WeComOutboundPriority.ContentDelta, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForSlotAsync_FinishPriority_ShouldBypassLimit()
    {
        var governor = new WeComOutboundGovernor();

        for (var i = 0; i < WeComOutboundGovernor.MaxMessagesPerMinute; i++)
        {
            governor.RecordSend(WeComOutboundPriority.ContentDelta);
        }

        var act = () => governor.WaitForSlotAsync(WeComOutboundPriority.Finish, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WaitForSlotAsync_KeepalivePriority_ShouldBypassContentLimit()
    {
        var governor = new WeComOutboundGovernor();

        for (var i = 0; i < WeComOutboundGovernor.MaxMessagesPerMinute; i++)
        {
            governor.RecordSend(WeComOutboundPriority.ContentDelta);
        }

        var act = () => governor.WaitForSlotAsync(WeComOutboundPriority.Keepalive, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordSend_Keepalive_ShouldNotConsumeContentQuota()
    {
        var governor = new WeComOutboundGovernor();

        for (var i = 0; i < WeComOutboundGovernor.MaxMessagesPerMinute; i++)
        {
            governor.RecordSend(WeComOutboundPriority.Keepalive);
        }

        var act = () => governor.WaitForSlotAsync(WeComOutboundPriority.ContentDelta, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
