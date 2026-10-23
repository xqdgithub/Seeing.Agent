using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
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
        var h = new ToolHarness();
        h.Sink.Setup(s => s.SendToUserAsync(
                "qq", "u1",
                It.Is<OutboundContent>(c => c.Text == "hi"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Fail(kind));
        var tool = h.Create();

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
        var h = new ToolHarness();
        h.Sink.Setup(s => s.SendToConversationAsync(
                It.IsAny<DeliveryTarget>(),
                It.Is<OutboundContent>(c => c.Text == "hi"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, "timeout"));
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            text = "hi"
        }), new ToolContext());

        result.Success.Should().BeFalse();
        result.Output.Should().Be("平台错误：timeout");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.Is<DeliveryTarget>(t =>
                t.Conversation.ChannelId == "qq"
                && t.Conversation.Kind == ConversationKind.Group
                && t.Conversation.ExternalId == "g1"),
            It.Is<OutboundContent>(c => c.Text == "hi"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendAttachments_WithInferredKindAndSize()
    {
        var h = new ToolHarness();
        h.SetupFile("/ws/report.pdf", 100);
        h.Sink.Setup(s => s.SendToConversationAsync(
                It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            attachments = new[] { new { path = "report.pdf" } }
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.Is<DeliveryTarget>(t => t.Conversation.ExternalId == "g1"),
            It.Is<OutboundContent>(c =>
                c.Text == null
                && c.Attachments.Count == 1
                && c.Attachments[0].Kind == OutboundAttachmentKind.File
                && c.Attachments[0].SizeBytes == 100
                && c.Attachments[0].LocalPath == "/ws/report.pdf"
                && c.Attachments[0].FileName == "report.pdf"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenPathIsOutsideWorkspace()
    {
        var h = new ToolHarness();
        h.SetupFile("/ws/secret.pdf", 100);
        h.Gate.Setup(g => g.EnsureWithinWorkspace(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("越界");
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            attachments = new[] { new { path = "secret.pdf" } }
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("越界");
        // 越界映射为 AttachmentUnavailable：文案前缀 + 门闸原因原样透传
        result.Error.Should().Be("附件不可用：越界");
        result.Title.Should().Be("投递失败");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenChannelIdGivenWithoutTarget()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            text = "hi"
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Contain("未指定目标对话");
        h.Resolver.Verify(r => r.ResolveSessionTargetAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnResolverFailure_WhenSessionHasNoRoute()
    {
        var h = new ToolHarness();
        h.Resolver.Setup(r => r.ResolveSessionTargetAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChannelTargetResolution.Fail(
                OutboundFailureKind.NotInDirectory, "当前会话及父会话均未绑定通道"));
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new { text = "hi" }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未绑定通道");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDefaultToSessionTarget_WhenNoTargetGiven()
    {
        var h = new ToolHarness();
        var target = new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g1"
            }
        };
        h.Resolver.Setup(r => r.ResolveSessionTargetAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChannelTargetResolution.Ok(target));
        h.Sink.Setup(s => s.SendToConversationAsync(
                It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new { text = "hi" }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.Is<DeliveryTarget>(t =>
                t.Conversation.ChannelId == "qq"
                && t.Conversation.Kind == ConversationKind.Group
                && t.Conversation.ExternalId == "g1"),
            It.Is<OutboundContent>(c => c.Text == "hi"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseSessionChannel_WhenExplicitTargetOmitsChannelId()
    {
        var h = new ToolHarness();
        h.Resolver.Setup(r => r.ResolveSessionTargetAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChannelTargetResolution.Ok(new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = "qq",
                    Kind = ConversationKind.Direct,
                    ExternalId = "self"
                }
            }));
        h.Sink.Setup(s => s.SendToUserAsync(
                "qq", "u1", It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var tool = h.Create();

        var result = await tool.ExecuteAsync(
            Args(new { targetUserId = "u1", text = "hi" }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        h.Sink.Verify(s => s.SendToUserAsync(
            "qq", "u1",
            It.Is<OutboundContent>(c => c.Text == "hi"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAppendChannelHint_WhenExplicitTargetHasNoChannel()
    {
        var h = new ToolHarness();
        h.Resolver.Setup(r => r.ResolveSessionTargetAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChannelTargetResolution.Fail(
                OutboundFailureKind.ChannelUnavailable, "通道投递未配置"));
        var tool = h.Create();

        var result = await tool.ExecuteAsync(
            Args(new { targetUserId = "u1", text = "hi" }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("投递失败");
        result.Error.Should().Contain("通道投递未配置").And.Contain("请提供 channelId");
        h.Sink.Verify(s => s.SendToUserAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenTargetUserIdAndConversationGiven()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            targetUserId = "u1",
            conversationExternalId = "g1",
            conversationKind = "group",
            text = "hi"
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Contain("二选一");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenContentIsMissing()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(
            Args(new { channelId = "qq", conversationExternalId = "g1", conversationKind = "group" }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Contain("至少提供一个");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenAttachmentsIsObject()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            text = "hi",
            attachments = new { path = "report.pdf" }
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Be("attachments 必须是数组");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAcceptJsonArrayString_ForAttachments()
    {
        var h = new ToolHarness();
        h.SetupFile("/ws/report.pdf", 100);
        h.Sink.Setup(s => s.SendToConversationAsync(
                It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            attachments = "[{\"path\":\"report.pdf\",\"kind\":\"file\",\"name\":\"report.pdf\"}]"
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.Is<DeliveryTarget>(t => t.Conversation.ExternalId == "g1"),
            It.Is<OutboundContent>(c =>
                c.Attachments.Count == 1
                && c.Attachments[0].LocalPath == "/ws/report.pdf"
                && c.Attachments[0].Kind == OutboundAttachmentKind.File
                && c.Attachments[0].SizeBytes == 100),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenAttachmentsStringIsNotJsonArray()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            text = "hi",
            attachments = "{\"path\":\"report.pdf\"}"
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Contain("不是 JSON 数组");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenAttachmentsStringIsInvalidJson()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            text = "hi",
            attachments = "[{"
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Contain("不是合法 JSON");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTreatExplicitNullAttachments_AsAbsent()
    {
        var h = new ToolHarness();
        h.Sink.Setup(s => s.SendToConversationAsync(
                It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            text = "hi",
            attachments = (object?)null
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(),
            It.Is<OutboundContent>(c => c.Text == "hi" && c.Attachments.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenAttachmentPathIsNotString()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            attachments = new object[] { new { path = 123 } }
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Be("attachments[].path 必须是字符串");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenAttachmentItemIsNotObject()
    {
        var h = new ToolHarness();
        var tool = h.Create();

        var result = await tool.ExecuteAsync(Args(new
        {
            channelId = "qq",
            conversationExternalId = "g1",
            conversationKind = "group",
            attachments = new object[] { "report.pdf" }
        }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Title.Should().Be("参数错误");
        result.Error.Should().Be("attachments 的每一项必须是对象");
        h.Sink.Verify(s => s.SendToConversationAsync(
            It.IsAny<DeliveryTarget>(), It.IsAny<OutboundContent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void ParametersSchema_ShouldAllowEmptyRequired_AndDescribeNestedItems()
    {
        var schema = new ToolHarness().Create().ParametersSchema;

        schema.GetProperty("required").GetArrayLength().Should().Be(0);

        var items = schema.GetProperty("properties").GetProperty("attachments").GetProperty("items");
        items.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();

        var properties = items.GetProperty("properties");
        properties.GetProperty("path").GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();

        var kind = properties.GetProperty("kind");
        var kindDescription = kind.GetProperty("description").GetString();
        kindDescription.Should().NotBeNullOrWhiteSpace();
        foreach (var value in new[] { "image", "video", "audio", "file" })
            kindDescription.Should().Contain(value);
        kind.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("image", "video", "audio", "file");
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}

public class ChannelAttachmentNormalizerTests
{
    [Fact]
    public void Normalize_ShouldReturnAttachmentUnavailable_WhenGateRejects()
    {
        var h = new ToolHarness();
        h.SetupFile("/ws/report.pdf", 100);
        h.Gate.Setup(g => g.EnsureWithinWorkspace(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("越界");
        var normalizer = h.CreateNormalizer();

        var normalized = normalizer.Normalize(
            "s1", JsonSerializer.SerializeToElement(new { path = "report.pdf" }));

        normalized.Attachment.Should().BeNull();
        normalized.Error.Should().Be("越界");
        normalized.IsParameterError.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, 0L)]   // 文件不存在
    [InlineData(true, true, 0L)]     // 是目录
    [InlineData(true, false, 0L)]    // 空文件
    public void Normalize_ShouldReturnAttachmentUnavailable_WhenFileNotUsable(
        bool exists, bool isDirectory, long length)
    {
        var h = new ToolHarness();
        const string full = "/ws/report.pdf";
        h.FileSystem.Setup(f => f.GetFullPath(It.IsAny<string>())).Returns(full);
        h.FileSystem.Setup(f => f.Exists(full)).Returns(exists);
        h.FileSystem.Setup(f => f.IsDirectory(full)).Returns(isDirectory);
        h.FileSystem.Setup(f => f.GetFileLength(full)).Returns(length);
        var normalizer = h.CreateNormalizer();

        var normalized = normalizer.Normalize(
            "s1", JsonSerializer.SerializeToElement(new { path = "report.pdf" }));

        normalized.Attachment.Should().BeNull();
        normalized.Error.Should().NotBeNullOrWhiteSpace();
        normalized.IsParameterError.Should().BeFalse();
    }

    [Fact]
    public void Normalize_ShouldInferKindFromExtension_WhenKindOmitted()
    {
        var h = new ToolHarness();
        h.SetupFile("/ws/clip.mp4", 2048);
        var normalizer = h.CreateNormalizer();

        var normalized = normalizer.Normalize(
            "s1", JsonSerializer.SerializeToElement(new { path = "clip.mp4" }));

        normalized.Error.Should().BeNull();
        normalized.Attachment!.Kind.Should().Be(OutboundAttachmentKind.Video);
        normalized.Attachment.SizeBytes.Should().Be(2048);
    }

    [Fact]
    public void Normalize_ShouldPreferExplicitKindAndName_WhenProvided()
    {
        // 扩展名是 .pdf（会推断为 File），显式 kind/name 必须胜出。
        var h = new ToolHarness();
        h.SetupFile("/ws/report.pdf", 100);
        var normalizer = h.CreateNormalizer();

        var normalized = normalizer.Normalize(
            "s1", JsonSerializer.SerializeToElement(new { path = "report.pdf", kind = "audio", name = "语音" }));

        normalized.Error.Should().BeNull();
        normalized.Attachment!.Kind.Should().Be(OutboundAttachmentKind.Audio);
        normalized.Attachment.FileName.Should().Be("语音");
        normalized.Attachment.LocalPath.Should().Be("/ws/report.pdf");
    }

    [Fact]
    public void Normalize_ShouldReturnParameterError_WhenNoWorkspaceAvailableForRelativePath()
    {
        // 会话 cwd 与项目根都拿不到 → 参数错误（提示改给绝对路径）。
        var h = new ToolHarness();
        h.SessionWorkspace.Setup(r => r.ResolveWorkingDirectory(It.IsAny<string>())).Returns((string?)null);
        h.Workspace.Setup(w => w.ProjectSeeingDirectory).Returns(string.Empty);
        var normalizer = h.CreateNormalizer();

        var normalized = normalizer.Normalize(
            "s1", JsonSerializer.SerializeToElement(new { path = "report.pdf" }));

        normalized.Attachment.Should().BeNull();
        normalized.IsParameterError.Should().BeTrue();
        normalized.Error.Should().Contain("缺少工作区");
        h.FileSystem.Verify(f => f.GetFullPath(It.IsAny<string>()), Times.Never);
    }
}

/// <summary>工具最小依赖装配：全部依赖均为必需（与生产 DI 一致）。</summary>
internal sealed class ToolHarness
{
    public Mock<IChannelMessageSink> Sink { get; } = new();
    public Mock<IChannelTargetResolver> Resolver { get; } = new();
    public Mock<IWorkspacePathGate> Gate { get; } = new();
    public Mock<ISessionWorkspaceResolver> SessionWorkspace { get; } = new();
    public Mock<IWorkspaceProvider> Workspace { get; } = new();
    public Mock<IFileSystem> FileSystem { get; } = new();

    public ToolHarness()
    {
        SessionWorkspace.Setup(r => r.ResolveWorkingDirectory(It.IsAny<string>())).Returns("/ws");
        Workspace.Setup(w => w.ProjectSeeingDirectory).Returns("/ws/.seeing");
        Gate.Setup(g => g.EnsureWithinWorkspace(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string?)null);
    }

    public SendMessageTool Create() => new(
        Sink.Object,
        Resolver.Object,
        Gate.Object,
        SessionWorkspace.Object,
        Workspace.Object,
        FileSystem.Object);

    public ChannelAttachmentNormalizer CreateNormalizer() => new(
        Gate.Object,
        SessionWorkspace.Object,
        Workspace.Object,
        FileSystem.Object);

    public void SetupFile(string fullPath, long size)
    {
        FileSystem.Setup(f => f.GetFullPath(It.IsAny<string>())).Returns(fullPath);
        FileSystem.Setup(f => f.Exists(fullPath)).Returns(true);
        FileSystem.Setup(f => f.IsDirectory(fullPath)).Returns(false);
        FileSystem.Setup(f => f.GetFileLength(fullPath)).Returns(size);
    }
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

public class NoopChannelTargetResolverTests
{
    [Fact]
    public async Task ResolveSessionTargetAsync_ShouldFailWithUnconfiguredDetail()
    {
        var resolver = new NoopChannelTargetResolver();

        var resolution = await resolver.ResolveSessionTargetAsync(
            "s1", TestContext.Current.CancellationToken);

        resolution.Target.Should().BeNull();
        resolution.FailureKind.Should().Be(OutboundFailureKind.ChannelUnavailable);
        resolution.Detail.Should().Be("通道投递未配置");
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
