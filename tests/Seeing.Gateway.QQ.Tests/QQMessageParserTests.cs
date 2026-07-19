using System.Text.Json;
using FluentAssertions;
using Seeing.Gateway.QQ;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQMessageParserTests
{
    [Fact]
    public void TryParse_C2C_ShouldExtractFields()
    {
        var json = """
        {
          "id": "msg1",
          "content": "hello <@!123>",
          "author": { "user_openid": "uopenid" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("C2C_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.MessageType.Should().Be("c2c");
        msg.MsgId.Should().Be("msg1");
        msg.SenderOpenId.Should().Be("uopenid");
        msg.Text.Should().Be("hello");
    }

    [Fact]
    public void TryParse_Group_ShouldExtractGroupOpenId()
    {
        var json = """
        {
          "id": "msg2",
          "content": "hi",
          "group_openid": "gopenid",
          "author": { "member_openid": "mopenid" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("GROUP_AT_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.MessageType.Should().Be("group");
        msg.GroupOpenId.Should().Be("gopenid");
        msg.SenderOpenId.Should().Be("mopenid");
    }
}
