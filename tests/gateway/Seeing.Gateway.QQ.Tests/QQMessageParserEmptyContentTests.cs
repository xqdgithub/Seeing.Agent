using FluentAssertions;
using Seeing.Gateway.Models;
using System.Text.Json;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

/// <summary>
/// 空内容语义：**不得**用空字符串 text part 掩盖「文本空 + 媒体全丢」。
/// <para>
/// 历史缺陷：<c>ToGatewayRequest</c> 在 parts 为空时补一个 <c>GatewayTextContentPart("")</c>，
/// 把真空伪装成「有内容」，上游无法拒绝，provider 侧只能报 400。
/// 纯媒体消息不在此列——媒体 part 已经在 parts 里。
/// </para>
/// </summary>
public class QQMessageParserEmptyContentTests
{
    private static ParsedQQMessage Message(string text, params QQAttachmentMeta[] attachments) => new()
    {
        MessageType = "c2c",
        MsgId = "m1",
        SenderOpenId = "u1",
        Text = text,
        Attachments = attachments
    };

    [Fact]
    public void ToGatewayRequest_TextOnly_ShouldProduceSingleTextPart()
    {
        var request = QQMessageParser.ToGatewayRequest(Message("你好"), "s1", null, null, null);

        request.Input.Should().ContainSingle();
        request.Input![0].Should().BeOfType<GatewayTextContentPart>();
        ((GatewayTextContentPart)request.Input[0]).Text.Should().Be("你好");
    }

    [Fact]
    public void ToGatewayRequest_TextEmptyAndNoMedia_ShouldNotFabricateEmptyTextPart()
    {
        var request = QQMessageParser.ToGatewayRequest(
            Message(""), "s1", null, null, null, extraParts: []);

        request.Input.Should().BeNullOrEmpty("真空内容必须显式为空，由上层拒绝，而不是伪造空 part");
    }

    [Fact]
    public void ToGatewayRequest_MediaOnly_ShouldProduceOnlyMediaPart()
    {
        var parts = new List<GatewayContentPart>
        {
            new GatewayImageContentPart("data:image/png;base64,AAAA", "image/png")
        };

        var request = QQMessageParser.ToGatewayRequest(
            Message(""), "s1", null, null, null, parts);

        request.Input.Should().ContainSingle();
        request.Input![0].Should().BeOfType<GatewayImageContentPart>();
        request.Input.OfType<GatewayTextContentPart>().Should().BeEmpty(
            "纯媒体消息不需要空文本占位");
    }

    [Fact]
    public void ToGatewayRequest_TextAndMedia_ShouldProduceBothParts()
    {
        var parts = new List<GatewayContentPart>
        {
            new GatewayImageContentPart("data:image/png;base64,AAAA", "image/png")
        };

        var request = QQMessageParser.ToGatewayRequest(
            Message("看图"), "s1", null, null, null, parts);

        request.Input.Should().HaveCount(2);
        request.Input!.OfType<GatewayTextContentPart>().Single().Text.Should().Be("看图");
        request.Input.OfType<GatewayImageContentPart>().Should().ContainSingle();
    }
}
