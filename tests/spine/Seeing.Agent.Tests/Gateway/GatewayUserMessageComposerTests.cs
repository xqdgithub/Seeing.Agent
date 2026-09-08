using FluentAssertions;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Gateway;

public class GatewayUserMessageComposerTests
{
    [Fact]
    public void Compose_InputOnly_ShouldPreserveExistingBehavior()
    {
        var message = GatewayUserMessageComposer.Compose(
            [new GatewayTextContentPart("hello")],
            quote: null);

        message.Should().NotBeNull();
        message!.Content.Should().Be("hello");
        message.Metadata.Should().BeNull();
    }

    [Fact]
    public void Compose_TextQuoteAndInput_ShouldUseXmlBoundaries()
    {
        var quote = new GatewayQuoteContext
        {
            MsgType = "text",
            SourceChannel = "wecom",
            Content = [new GatewayTextContentPart("被引用的原始内容")]
        };

        var message = GatewayUserMessageComposer.Compose(
            [new GatewayTextContentPart("数据来源是什么")],
            quote);

        message.Should().NotBeNull();
        message!.Content.Should().Contain("<quoted_message type=\"text\" source=\"wecom\">");
        message.Content.Should().Contain("被引用的原始内容");
        message.Content.Should().Contain("<user_message>");
        message.Content.Should().Contain("数据来源是什么");
        message.Content.Should().Contain("</quoted_message>");
        message.Content.Should().Contain("</user_message>");
        message.Metadata.Should().ContainKey("has_quote");
        message.Metadata!["has_quote"].Should().Be(true);
        message.Metadata["quote_msgtype"].Should().Be("text");
        message.Metadata["quote_source"].Should().Be("wecom");
    }

    [Fact]
    public void Compose_QuoteWithImage_ShouldUseMultipartMessage()
    {
        var quote = new GatewayQuoteContext
        {
            MsgType = "mixed",
            SourceChannel = "wecom",
            Content =
            [
                new GatewayTextContentPart("引用说明"),
                new GatewayImageContentPart("data:image/png;base64,abc123", "image/png")
            ]
        };

        var message = GatewayUserMessageComposer.Compose(
            [new GatewayTextContentPart("这是什么")],
            quote);

        message.Should().NotBeNull();
        message!.IsMultimodal.Should().BeTrue();
        message.Parts.Should().NotBeNull();
        message.Parts!.Should().HaveCountGreaterThan(2);
        message.Parts[0].Text.Should().Contain("<quoted_message");
        message.Parts.Should().Contain(p => p.Type == ContentPartType.Image);
        message.Parts[^1].Type.Should().Be(ContentPartType.Text);
        message.Parts[^1].Text.Should().Contain("</user_message>");
    }

    [Fact]
    public void Compose_EscapesXmlInUserText()
    {
        var quote = new GatewayQuoteContext
        {
            MsgType = "text",
            SourceChannel = "wecom",
            Content = [new GatewayTextContentPart("a < b")]
        };

        var message = GatewayUserMessageComposer.Compose(
            [new GatewayTextContentPart("c & d")],
            quote);

        message!.Content.Should().Contain("a &lt; b");
        message.Content.Should().Contain("c &amp; d");
    }
}
