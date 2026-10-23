using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tools.ChannelMessaging.Tests;

public class SendMessageToolTests
{
    [Theory]
    [InlineData(OutboundFailureKind.NotInDirectory, "该用户尚未与机器人建立对话，无法推送")]
    [InlineData(OutboundFailureKind.OptedOut, "用户已退订")]
    [InlineData(OutboundFailureKind.RateLimited, "超出投递频率限制")]
    [InlineData(OutboundFailureKind.ChannelUnavailable, "通道投递未配置或通道不可用")]
    public async Task ExecuteAsync_UserTargetFailureKind_ShouldReturnChineseMessage(
        OutboundFailureKind kind, string expected)
    {
        var sink = new Mock<IChannelMessageSink>();
        sink.Setup(s => s.SendToUserAsync(
                "qq", "u1",
                It.Is<OutboundContent>(c => c.Text == "hi"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Fail(kind));
        var tool = new SendMessageTool(sink.Object);

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            targetUserId = "u1",
            text = "hi"
        }), new ToolContext());

        result.Success.Should().BeFalse();
        result.Output.Should().Be(expected);
        result.Error.Should().Be(expected);
    }

    [Fact]
    public async Task ExecuteAsync_PlatformError_ShouldIncludeDetail()
    {
        var sink = new Mock<IChannelMessageSink>();
        sink.Setup(s => s.SendToConversationAsync(
                It.IsAny<DeliveryTarget>(),
                It.Is<OutboundContent>(c => c.Text == "hi"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, "timeout"));
        var tool = new SendMessageTool(sink.Object);

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            text = "hi"
        }), new ToolContext());

        result.Success.Should().BeFalse();
        result.Output.Should().Be("平台错误：timeout");
        sink.Verify(s => s.SendToConversationAsync(
            It.Is<DeliveryTarget>(t =>
                t.Conversation.ChannelId == "qq"
                && t.Conversation.Kind == ConversationKind.Group
                && t.Conversation.ExternalId == "g1"),
            It.Is<OutboundContent>(c => c.Text == "hi"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}

public class NoopChannelMessageSinkTests
{
    [Fact]
    public async Task SendToUserAsync_ShouldFailWithUnconfiguredDetail()
    {
        var sink = new NoopChannelMessageSink();

        var result = await sink.SendToUserAsync(
            "qq", "u1", new OutboundContent { Text = "hi" }, TestContext.Current.CancellationToken);

        result.Delivered.Should().BeFalse();
        result.FailureKind.Should().Be(OutboundFailureKind.ChannelUnavailable);
        result.Detail.Should().Be("通道投递未配置");
    }

    [Fact]
    public async Task SendToConversationAsync_ShouldFailWithUnconfiguredDetail()
    {
        var sink = new NoopChannelMessageSink();
        var target = new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Direct,
                ExternalId = "u1"
            }
        };

        var result = await sink.SendToConversationAsync(
            target, new OutboundContent { Text = "hi" }, TestContext.Current.CancellationToken);

        result.Delivered.Should().BeFalse();
        result.FailureKind.Should().Be(OutboundFailureKind.ChannelUnavailable);
        result.Detail.Should().Be("通道投递未配置");
    }
}

public class ListChannelDirectoryToolTests
{
    [Fact]
    public async Task ListUsers_ShouldPassSkipAndTake()
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.ListUsersAsync("qq", 10, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ChannelUserEntry
                {
                    ChannelId = "qq",
                    ExternalId = "u1",
                    DisplayName = "Ada",
                    FirstSeenAt = DateTimeOffset.UnixEpoch,
                    LastSeenAt = DateTimeOffset.UnixEpoch,
                    OptedOut = false
                }
            ]);
        var tool = new ListChannelUsersTool(directory.Object);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { channelId = "qq", skip = 10, take = 20 }),
            new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("u1");
        directory.Verify(d => d.ListUsersAsync("qq", 10, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListConversations_ShouldPassSkipAndTake()
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.ListConversationsAsync("wecom", 5, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ConversationEntry
                {
                    Conversation = new ConversationInfo
                    {
                        ChannelId = "wecom",
                        Kind = ConversationKind.Guild,
                        ExternalId = "c1",
                        Title = "大厅"
                    },
                    FirstSeenAt = DateTimeOffset.UnixEpoch,
                    LastSeenAt = DateTimeOffset.UnixEpoch,
                    MemberExternalIds = []
                }
            ]);
        var tool = new ListConversationsTool(directory.Object);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { channelId = "wecom", skip = 5, take = 15 }),
            new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("c1");
        directory.Verify(d => d.ListConversationsAsync("wecom", 5, 15, It.IsAny<CancellationToken>()), Times.Once);
    }
}
