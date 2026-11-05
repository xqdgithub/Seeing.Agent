using FluentAssertions;
using Seeing.Agent.Abstractions.Models;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.Tests.Core;

/// <summary>
/// 网关内容段 → <see cref="ChatAttachment"/> 的单一映射契约。
/// <para>
/// 钉住的核心不变量（I2）：**进入 runtime 后媒体一律是纯 base64 + MIME 两个正交字段**。
/// 历史缺陷：<c>Base64Data = img.Url</c> 把完整 data URL 塞进要求纯 base64 的字段，
/// 下游 provider 出口再拼一次 <c>data:{mime};base64,{...}</c> → 双前缀 → 400。
/// </para>
/// </summary>
public class GatewayContentPartMapperTests
{
    [Fact]
    public void ToChatInput_ImageDataUrl_ShouldStripPrefixIntoPureBase64()
    {
        var input = GatewayContentPartMapper.ToChatInput(
            [new GatewayImageContentPart("data:image/png;base64,iVBORw0KGgo=", "image/png")],
            quote: null);

        input.Attachments.Should().ContainSingle();
        var attachment = input.Attachments![0];
        attachment.Base64Data.Should().Be("iVBORw0KGgo=", "纯 base64 不得包含 data: 前缀");
        attachment.Base64Data.Should().NotContain("data:");
        attachment.Base64Data.Should().NotContain(";base64,");
        attachment.MimeType.Should().Be("image/png");
    }

    [Fact]
    public void ToChatInput_ImageDataUrl_WithoutExplicitMime_ShouldTakeMimeFromUrl()
    {
        var input = GatewayContentPartMapper.ToChatInput(
            [new GatewayImageContentPart("data:image/jpeg;base64,/9j/4AAQ")],
            quote: null);

        var attachment = input.Attachments!.Single();
        attachment.Base64Data.Should().Be("/9j/4AAQ");
        attachment.MimeType.Should().Be("image/jpeg", "data URL 自带 MIME 时应作为回退来源");
    }

    [Fact]
    public void ToChatInput_ImageHttpsUrl_ShouldPassThroughAsIs()
    {
        var input = GatewayContentPartMapper.ToChatInput(
            [new GatewayImageContentPart("https://cdn.example.com/a.png", "image/png")],
            quote: null);

        var attachment = input.Attachments!.Single();
        attachment.Base64Data.Should().Be("https://cdn.example.com/a.png");
        attachment.MimeType.Should().Be("image/png");
    }

    [Fact]
    public void ToChatInput_FileDataUrl_ShouldStripPrefixAndKeepName()
    {
        var input = GatewayContentPartMapper.ToChatInput(
            [new GatewayFileContentPart("data:application/pdf;base64,JVBERi0=", "application/pdf", "a.pdf")],
            quote: null);

        var attachment = input.Attachments!.Single();
        attachment.Base64Data.Should().Be("JVBERi0=");
        attachment.Base64Data.Should().NotContain("data:");
        attachment.MimeType.Should().Be("application/pdf");
        attachment.FileName.Should().Be("a.pdf");
    }

    [Fact]
    public void ToChatInput_TextPlusImage_ShouldKeepTextAndAttachment()
    {
        var input = GatewayContentPartMapper.ToChatInput(
            [
                new GatewayTextContentPart("这是什么"),
                new GatewayImageContentPart("data:image/png;base64,AAAA", "image/png")
            ],
            quote: null);

        input.Text.Should().Be("这是什么");
        input.Attachments.Should().ContainSingle();
        input.Attachments![0].Base64Data.Should().Be("AAAA");
    }

    [Fact]
    public void ToChatInput_EmptyInput_ShouldReturnEmptyChatInput()
    {
        var input = GatewayContentPartMapper.ToChatInput(null, quote: null);

        input.Text.Should().BeNull();
        input.Attachments.Should().BeNull();
    }

    [Fact]
    public void ToChatInput_QuoteWithImage_ShouldStripPrefixForBothSides()
    {
        var quote = new GatewayQuoteContext
        {
            MsgType = "mixed",
            SourceChannel = "wecom",
            Content = [new GatewayImageContentPart("data:image/png;base64,QUOTED", "image/png")]
        };

        var input = GatewayContentPartMapper.ToChatInput(
            [new GatewayImageContentPart("data:image/png;base64,USER", "image/png")],
            quote);

        input.Text.Should().Contain("<quoted_message");
        input.Text.Should().Contain("<user_message>");
        input.Attachments.Should().HaveCount(2, "引用图片与用户图片都要保留");
        input.Attachments!.Should().OnlyContain(a => !a.Base64Data.StartsWith("data:"));
        input.Attachments!.Select(a => a.Base64Data).Should().BeEquivalentTo(["QUOTED", "USER"]);
    }

    [Fact]
    public void ToChatInput_QuoteText_ShouldUseXmlBoundaries()
    {
        var quote = new GatewayQuoteContext
        {
            MsgType = "text",
            SourceChannel = "wecom",
            Content = [new GatewayTextContentPart("被引用的原始内容")]
        };

        var input = GatewayContentPartMapper.ToChatInput(
            [new GatewayTextContentPart("数据来源是什么")],
            quote);

        input.Text.Should().Contain("<quoted_message type=\"text\" source=\"wecom\">");
        input.Text.Should().Contain("被引用的原始内容");
        input.Text.Should().Contain("<user_message>");
        input.Text.Should().Contain("数据来源是什么");
        input.Text.Should().Contain("</quoted_message>");
        input.Text.Should().Contain("</user_message>");
        input.Attachments.Should().BeNull("纯文本引用不产生附件");
    }

    [Fact]
    public void ToChatInput_QuoteText_ShouldEscapeXml()
    {
        var quote = new GatewayQuoteContext
        {
            MsgType = "text",
            SourceChannel = "wecom",
            Content = [new GatewayTextContentPart("a < b")]
        };

        var input = GatewayContentPartMapper.ToChatInput(
            [new GatewayTextContentPart("c & d")],
            quote);

        input.Text.Should().Contain("a &lt; b");
        input.Text.Should().Contain("c &amp; d");
    }

    [Fact]
    public void ChatAttachmentImageFactory_ShouldStripDataPrefix()
    {
        var attachment = ChatAttachment.Image("a.png", "data:image/png;base64,ZZZZ", "image/png");

        attachment.Base64Data.Should().Be("ZZZZ", "工厂构造的附件天生满足「纯 base64」不变量");
    }

    [Fact]
    public void ChatAttachmentFileFactory_ShouldStripDataPrefix()
    {
        var attachment = ChatAttachment.File("a.pdf", "data:application/pdf;base64,YYYY", "application/pdf");

        attachment.Base64Data.Should().Be("YYYY");
    }
}
