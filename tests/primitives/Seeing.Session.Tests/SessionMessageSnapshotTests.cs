using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests;

/// <summary>消息快照缓存：同版本复用实例、写入后失效、Id 索引可查。</summary>
public class SessionMessageSnapshotTests
{
    private static SessionData NewSession() => SessionData.Create();

    [Fact]
    public void Messages_WithoutMutation_ShouldReturnSameInstance()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" });

        s.Messages.Should().BeSameAs(s.Messages);
    }

    [Fact]
    public void Messages_AfterAdd_ShouldInvalidate()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" });
        var before = s.Messages;

        s.AddMessage(new SessionMessage { Id = "m2", Role = MessageRole.Assistant, Content = "二" });

        s.Messages.Should().NotBeSameAs(before);
        s.Messages.Should().HaveCount(2);
    }

    [Fact]
    public void Messages_AfterRemove_ShouldReflectRemoval()
    {
        var s = NewSession();
        var msg = new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" };
        s.AddMessage(msg);
        _ = s.Messages;

        s.RemoveMessage(msg);

        s.Messages.Should().BeEmpty();
    }

    [Fact]
    public void TryGetMessage_WhenExists_ShouldReturnMessage()
    {
        var s = NewSession();
        var msg = new SessionMessage { Id = "abc", Role = MessageRole.User, Content = "内容" };
        s.AddMessage(msg);

        s.TryGetMessage("abc", out var found).Should().BeTrue();
        found.Should().BeSameAs(msg);
    }

    [Fact]
    public void TryGetMessage_WhenMissing_ShouldReturnFalse()
        => NewSession().TryGetMessage("nope", out _).Should().BeFalse();

    [Fact]
    public void TryGetMessage_WhenIdNull_ShouldReturnFalse()
        => NewSession().TryGetMessage(null, out _).Should().BeFalse();

    [Fact]
    public void TryGetMessage_AfterRemove_ShouldReturnFalse()
    {
        var s = NewSession();
        var msg = new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" };
        s.AddMessage(msg);

        s.RemoveMessage(msg);

        s.TryGetMessage("m1", out _).Should().BeFalse();
    }

    [Fact]
    public void GetActiveMessages_WithSummary_ShouldStartAtLastSummary()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "old", Role = MessageRole.User, Content = "旧" });
        s.AddMessage(new SessionMessage { Id = "sum", Role = MessageRole.Assistant, Content = "摘要", IsSummary = true });
        s.AddMessage(new SessionMessage { Id = "new", Role = MessageRole.User, Content = "新" });

        var active = s.GetActiveMessages();

        active.Should().HaveCount(2);
        active[0].Id.Should().Be("sum");
    }

    /// <summary>Review Focus：GetActiveMessages 返回的必须是独立副本，调用方修改不得污染会话真相。</summary>
    [Fact]
    public void GetActiveMessages_ReturnedList_ShouldBeIndependent()
    {
        var s = NewSession();
        s.AddMessage(new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "一" });

        var active = s.GetActiveMessages();
        active.Clear();

        s.Messages.Should().HaveCount(1);
        s.MessageCount.Should().Be(1);
    }
}
