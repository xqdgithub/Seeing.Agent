using FluentAssertions;
using Seeing.Gateway.QQ;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQSessionTargetParserTests
{
    [Fact]
    public void TryParse_GroupSharedSession_ShouldExtractGroupOpenId()
    {
        var ok = QQSessionTargetParser.TryParse(
            "qq_group_CF4097D8267E9CC5378537A8A6BE65DA",
            out var target);

        ok.Should().BeTrue();
        target!.MessageType.Should().Be("group");
        target.GroupOpenId.Should().Be("CF4097D8267E9CC5378537A8A6BE65DA");
        target.SenderOpenId.Should().BeNull();
        target.MsgId.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_GroupPerMemberSession_ShouldExtractGroupAndSender()
    {
        var ok = QQSessionTargetParser.TryParse(
            "qq_group_GROUPID_MEMBERID",
            out var target);

        ok.Should().BeTrue();
        target!.GroupOpenId.Should().Be("GROUPID");
        target.SenderOpenId.Should().Be("MEMBERID");
    }

    [Fact]
    public void TryParse_C2C_ShouldExtractSender()
    {
        var ok = QQSessionTargetParser.TryParse("qq_USEROPENID", out var target);

        ok.Should().BeTrue();
        target!.MessageType.Should().Be("c2c");
        target.SenderOpenId.Should().Be("USEROPENID");
    }

    [Fact]
    public void TryParse_Channel_ShouldExtractChannelId()
    {
        var ok = QQSessionTargetParser.TryParse("qq_channel_12345", out var target);

        ok.Should().BeTrue();
        target!.MessageType.Should().Be("guild");
        target.ChannelId.Should().Be("12345");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("qq_unknown")]
    [InlineData("other_session")]
    public void TryParse_Invalid_ShouldFail(string? sessionId)
    {
        QQSessionTargetParser.TryParse(sessionId, out _).Should().BeFalse();
    }
}
