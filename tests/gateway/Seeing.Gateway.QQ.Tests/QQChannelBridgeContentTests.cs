using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Protocol;
using System.Text.Json;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

/// <summary>
/// 出站内容防御路径：桥的完整构造依赖真实 WS / HTTP / 目录，测试设施过重；
/// 这里直接锁定 handler 依赖的内部纯函数与协议反序列化事实——
/// 显式 <c>"attachments": null</c> 不抛，content 缺失返回 null。
/// </summary>
public class QQChannelBridgeContentTests
{
    [Fact]
    public void NormalizeOutboundContent_NullContent_ShouldReturnNull()
    {
        QQChannelBridge.NormalizeOutboundContent(null).Should().BeNull();
    }

    [Fact]
    public void NormalizeOutboundContent_NullAttachments_ShouldUseEmptyList()
    {
        var content = new OutboundContent { Text = "hi", Attachments = null! };

        var normalized = QQChannelBridge.NormalizeOutboundContent(content)!;

        normalized.Text.Should().Be("hi");
        normalized.Attachments.Should().BeEmpty();
    }

    /// <summary>
    /// 空内容提示必须区分两种病因：用户根本没发正文（群聊只 @ 不打字）vs 附件下载失败。
    /// 历史缺陷：两者共用「图片等附件可能下载失败」，纯 @ 提及被误报成下载失败。
    /// </summary>
    [Fact]
    public void DescribeEmptyInbound_NoAttachments_ShouldBlameMissingTextNotDownload()
    {
        var text = QQChannelBridge.DescribeEmptyInbound(attachmentCount: 0);

        text.Should().NotContain("下载失败", "没有任何附件时不存在下载失败");
        text.Should().Contain("@");
    }

    [Fact]
    public void DescribeEmptyInbound_WithAttachments_ShouldReportDownloadFailure()
    {
        var text = QQChannelBridge.DescribeEmptyInbound(attachmentCount: 2);

        text.Should().Contain("下载失败");
    }

    [Fact]
    public void DescribeEmptyInbound_BothBranches_ShouldDiffer()
    {
        QQChannelBridge.DescribeEmptyInbound(0)
            .Should().NotBe(QQChannelBridge.DescribeEmptyInbound(1));
    }

    [Fact]
    public void EmptyInboundWithUninitializedRaw_ShouldStillProduceDiagnostic()
    {
        // Raw 未赋值（ValueKind=Undefined）时取原始文本必须不抛，否则诊断日志会吃掉用户可见提示
        QQChannelBridge.SafeRawText(default).Should().Be("(unavailable)");
    }

    [Fact]
    public void EmptyInboundWithRaw_ShouldTruncateTo500Chars()
    {
        using var doc = JsonDocument.Parse(
            $$"""{"id":"m1","content":"{{new string('x', 900)}}"}""");

        var text = QQChannelBridge.SafeRawText(doc.RootElement);

        text.Length.Should().BeLessThanOrEqualTo(501, "超过 500 字符的 payload 必须截断");
    }

    [Fact]
    public void OutboundPayload_WithNullAttachments_ShouldDeserializeWithoutThrowing()
    {
        const string json = """
            {"channel":"qq","correlationSessionId":"s1","content":{"text":"hi","attachments":null}}
            """;

        var payload = JsonSerializer.Deserialize<GatewayChannelOutboundPayload>(
            json, GatewayWsFrameSerializer.JsonOptions)!;

        // 反序列化会把显式 null 原样留在 DTO 上（非 null 声明），桥必须在解引用前兜底
        payload.Content.Attachments.Should().BeNull();
        QQChannelBridge.NormalizeOutboundContent(payload.Content)!.Attachments.Should().BeEmpty();
    }
}
