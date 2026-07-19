using FluentAssertions;
using Seeing.Gateway.QQ;
using Seeing.Gateway.QQ.Connection;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQHttpApiClientBodyTests
{
    private static ParsedQQMessage Msg(string type, string msgId = "m1") => new()
    {
        MessageType = type,
        MsgId = msgId,
        SenderOpenId = "u1",
        GroupOpenId = "g1",
        ChannelId = "c1",
        GuildId = "guild1"
    };

    [Fact]
    public void BuildTextMessageBody_C2C_Markdown_ShouldNotSetTopLevelContent()
    {
        var seq = 0;
        var body = QQHttpApiClient.BuildTextMessageBody(
            Msg("c2c"), "hello", useMarkdown: true, keyboard: null, _ => ++seq);

        body.Should().ContainKey("markdown");
        body.Should().ContainKey("msg_type").WhoseValue.Should().Be(2);
        body.Should().ContainKey("msg_seq").WhoseValue.Should().Be(1);
        body.Should().ContainKey("msg_id");
        body.Should().NotContainKey("content");
    }

    [Fact]
    public void BuildTextMessageBody_C2C_Plain_ShouldUseMsgType0()
    {
        var body = QQHttpApiClient.BuildTextMessageBody(
            Msg("c2c"), "hello", useMarkdown: false, keyboard: null, _ => 3);

        body["content"].Should().Be("hello");
        body["msg_type"].Should().Be(0);
        body["msg_seq"].Should().Be(3);
    }

    [Fact]
    public void BuildTextMessageBody_Guild_ShouldOmitMsgTypeAndSeq()
    {
        var body = QQHttpApiClient.BuildTextMessageBody(
            Msg("guild"), "hello", useMarkdown: false, keyboard: null, _ => 99);

        body["content"].Should().Be("hello");
        body.Should().NotContainKey("msg_type");
        body.Should().NotContainKey("msg_seq");
        body.Should().ContainKey("msg_id");
    }

    [Fact]
    public void BuildTextMessageBody_GuildMarkdown_ShouldOmitMsgType()
    {
        var body = QQHttpApiClient.BuildTextMessageBody(
            Msg("guild"), "hello", useMarkdown: true, keyboard: null, _ => 1);

        body.Should().ContainKey("markdown");
        body.Should().NotContainKey("msg_type");
        body.Should().NotContainKey("msg_seq");
        body.Should().NotContainKey("content");
    }

    [Fact]
    public void TryParse_EmptyContentNoAttachments_ShouldFail()
    {
        var json = """{ "id": "msg-empty", "content": "  ", "author": { "user_openid": "u1" } }""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        QQMessageParser.TryParse("C2C_MESSAGE_CREATE", doc.RootElement, out _).Should().BeFalse();
    }
}
