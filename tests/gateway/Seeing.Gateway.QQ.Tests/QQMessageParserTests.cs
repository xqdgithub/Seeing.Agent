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

    [Fact]
    public void TryParse_Group_OnlyMention_ShouldStillParse()
    {
        // 群聊 AT：content 经常只有 <@!>，剥掉后为空，不能丢弃
        var json = """
        {
          "id": "msg-at-only",
          "content": "<@!123456789>",
          "group_openid": "gopenid",
          "author": { "member_openid": "mopenid" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("GROUP_AT_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.MessageType.Should().Be("group");
        msg.Text.Should().BeEmpty();
        msg.GroupOpenId.Should().Be("gopenid");
    }

    [Fact]
    public void TryParse_Group_OpenIdMention_ShouldStripAndKeepText()
    {
        var json = """
        {
          "id": "msg-at-hex",
          "content": "<@!E4F4AEA33253A2797FB897C50B81D7ED> 你好",
          "group_openid": "gopenid",
          "author": { "member_openid": "mopenid" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("GROUP_AT_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.Text.Should().Be("你好");
    }

    [Fact]
    public void TryParse_BotAuthor_ShouldFlag()
    {
        var json = """
        {
          "id": "msg-bot",
          "content": "收到，正在处理…",
          "author": { "user_openid": "bot1", "bot": true }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("C2C_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.IsBotAuthor.Should().BeTrue();
    }

    [Fact]
    public void TryParse_C2C_EmptyContentNoAttachments_ShouldFail()
    {
        var json = """{ "id": "msg-empty", "content": "  ", "author": { "user_openid": "u1" } }""";
        using var doc = JsonDocument.Parse(json);
        QQMessageParser.TryParse("C2C_MESSAGE_CREATE", doc.RootElement, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_GroupMessageCreate_ShouldParseAsGroup()
    {
        // 新版 QQ 群事件类型为 GROUP_MESSAGE_CREATE（非 GROUP_AT_MESSAGE_CREATE）
        var json = """
        {
          "id": "msg-gmc",
          "content": "<@79BA430F9D196620365D852A573F85CE> ？",
          "group_openid": "CF4097D8267E9CC5378537A8A6BE65DA",
          "author": { "member_openid": "D36EB17CD2893C3FCC916271DEAE34D2", "bot": false }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("GROUP_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.MessageType.Should().Be("group");
        msg.GroupOpenId.Should().Be("CF4097D8267E9CC5378537A8A6BE65DA");
        msg.Text.Should().Be("？");
        msg.IsBotAuthor.Should().BeFalse();
    }
}
