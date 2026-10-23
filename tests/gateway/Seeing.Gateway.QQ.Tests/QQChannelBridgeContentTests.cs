using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Protocol;
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
