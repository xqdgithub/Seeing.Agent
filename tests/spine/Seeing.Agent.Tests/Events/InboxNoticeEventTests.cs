using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class InboxNoticeEventTests
{
    [Fact]
    public void Type_ShouldBeInboxNotice()
    {
        IMessageEvent evt = new InboxNoticeEvent { SessionId = "s1", SenderKind = "session" };
        evt.Type.Should().Be(MessageEventType.InboxNotice);
        MessageEventType.InboxNotice.Should().Be("inbox.notice");
    }
}
