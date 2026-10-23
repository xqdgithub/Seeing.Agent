using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Models;
using Seeing.Gateway.QQ.Connection;
using Seeing.Gateway.QQ.Delivery;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQChannelAdapterTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly List<string> _tempFiles = [];

    [Theory]
    [InlineData(ConversationKind.Direct, "c2c", "/v2/users/u1/messages")]
    [InlineData(ConversationKind.Group, "group", "/v2/groups/g1/messages")]
    [InlineData(ConversationKind.Guild, "guild", "/channels/c1/messages")]
    public void ToParsed_Kind_ShouldSelectPathAndOpenId(ConversationKind kind, string messageType, string path)
    {
        var target = Target(kind, replyTo: "mid");

        var parsed = QQChannelAdapter.ToParsed(target);

        parsed.MessageType.Should().Be(messageType);
        parsed.MsgId.Should().Be("mid");
        QQHttpApiClient.ResolveSendPath(parsed).Should().Be(path);
        OpenId(parsed).Should().Be(kind == ConversationKind.Group ? "g1" : kind == ConversationKind.Guild ? "c1" : "u1");

        var body = QQHttpApiClient.BuildTextMessageBody(parsed, "hi", useMarkdown: false, keyboard: null, _ => 1);
        body.Should().ContainKey("msg_id").WhoseValue.Should().Be("mid");
    }

    [Fact]
    public void ToParsed_Group_ShouldKeepRecipientAsSenderOpenId()
    {
        var parsed = QQChannelAdapter.ToParsed(
            Target(ConversationKind.Group, replyTo: "mid", recipient: "member1"));

        parsed.GroupOpenId.Should().Be("g1");
        parsed.SenderOpenId.Should().Be("member1");
    }

    [Fact]
    public void ApplyPassiveWindow_ExpiredWindow_ShouldOmitMsgId()
    {
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), 2, () => T0.AddSeconds(301));
        tracker.RegisterInbound("mid", T0);
        var (adapter, _) = CreateAdapter(tracker: tracker);

        var parsed = adapter.ApplyPassiveWindow(
            QQChannelAdapter.ToParsed(Target(ConversationKind.Direct, replyTo: "mid")));

        parsed.MsgId.Should().BeEmpty();
        var body = QQHttpApiClient.BuildTextMessageBody(parsed, "push", useMarkdown: false, keyboard: null, _ => 1);
        body.Should().NotContainKey("msg_id");
    }

    [Fact]
    public void ApplyPassiveWindow_WithoutReplyTo_ShouldOmitMsgId()
    {
        var (adapter, _) = CreateAdapter();

        var parsed = adapter.ApplyPassiveWindow(
            QQChannelAdapter.ToParsed(Target(ConversationKind.Direct, replyTo: null)));

        parsed.MsgId.Should().BeEmpty();
        QQHttpApiClient.BuildTextMessageBody(parsed, "push", false, null, _ => 1)
            .Should().NotContainKey("msg_id");
    }

    [Fact]
    public async Task SendAsync_WithTwoAttachments_ShouldAcquirePassivePerPlatformMessage()
    {
        // 1 段文本 + 1 个附件 = 两条平台消息；额度 1 只够第一条（文本）带 msg_id
        var (adapter, handler) = CreateAdapter(tracker: CreateTracker(replyLimit: 1));
        var file = CreateFile("a.png", 16);
        var message = Message(
            ConversationKind.Direct,
            text: "说明",
            Attachment(OutboundAttachmentKind.Image, file, "a.png"));

        var result = await adapter.SendAsync(message);

        result.Delivered.Should().BeTrue();
        var textBodies = handler.Requests.Where(r => r.IsTextMessage).Select(r => r.Body).ToList();
        var mediaBodies = handler.Requests.Where(r => r.IsMediaMessage).Select(r => r.Body).ToList();
        textBodies.Should().ContainSingle().Which.Should().Contain("\"msg_id\"");
        mediaBodies.Should().ContainSingle().Which.Should().NotContain("\"msg_id\"");
    }

    [Fact]
    public async Task SendAsync_ShouldReportPartialFailure_WhenSecondAttachmentFails()
    {
        var (adapter, handler) = CreateAdapter();
        var uploads = 0;
        handler.OnRequest = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                uploads++;
                return uploads == 1
                    ? Json("""{"file_info":"fi-1"}""")
                    : Json("""{"message":"upload failed"}""", HttpStatusCode.InternalServerError);
            }

            return Json("{}");
        };

        var first = CreateFile("ok.png", 16);
        var second = CreateFile("bad.png", 16);
        var message = Message(
            ConversationKind.Direct,
            text: null,
            Attachment(OutboundAttachmentKind.Image, first, "ok.png"),
            Attachment(OutboundAttachmentKind.Image, second, "bad.png"));

        var result = await adapter.SendAsync(message);

        result.Delivered.Should().BeFalse();
        result.FailureKind.Should().Be(OutboundFailureKind.PlatformError);
        result.Detail.Should().Contain("1").And.Contain("2");
    }

    [Fact]
    public async Task SendAsync_ShouldMapOversizedFile_ToAttachmentTooLarge_BeforeReading()
    {
        var options = new QQOptions { MaxMediaBytes = 8 };
        var (adapter, handler) = CreateAdapter(options);
        var file = CreateFile("big.png", 16);
        var message = Message(
            ConversationKind.Direct,
            text: null,
            Attachment(OutboundAttachmentKind.Image, file, "big.png"));

        var result = await adapter.SendAsync(message);

        result.FailureKind.Should().Be(OutboundFailureKind.AttachmentTooLarge);
        handler.Requests.Count(r => r.IsUpload).Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_ShouldPropagateCancellation_WithoutSendingRemainingItems()
    {
        var (adapter, handler) = CreateAdapter();
        using var cts = new CancellationTokenSource();
        handler.OnRequest = (request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                && !body.Contains("\"msg_type\":7", StringComparison.Ordinal))
            {
                // 第 1 条文本发出后取消
                cts.Cancel();
            }

            return Json("""{"file_info":"fi-1"}""");
        };

        var file = CreateFile("a.png", 16);
        var message = Message(
            ConversationKind.Direct,
            text: "第一段",
            Attachment(OutboundAttachmentKind.Image, file, "a.png"));

        await FluentActions.Awaiting(() => adapter.SendAsync(message, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        handler.Requests.Where(r => r.IsMediaMessage).Should().BeEmpty();
        handler.Requests.Count(r => r.IsUpload).Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_ShouldRejectNonImage_ForGuild()
    {
        var (adapter, _) = CreateAdapter();
        var file = CreateFile("doc.bin", 16);
        var message = Message(
            ConversationKind.Guild,
            text: null,
            Attachment(OutboundAttachmentKind.File, file, "doc.bin"));

        var result = await adapter.SendAsync(message);

        result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnsupported);
    }

    [Theory]
    [InlineData(OutboundAttachmentKind.Image, "a.png", QQHttpApiClient.MediaTypeImage)]
    [InlineData(OutboundAttachmentKind.Video, "a.mp4", QQHttpApiClient.MediaTypeVideo)]
    [InlineData(OutboundAttachmentKind.Audio, "a.mp3", QQHttpApiClient.MediaTypeAudio)]
    [InlineData(OutboundAttachmentKind.File, "a.pdf", QQHttpApiClient.MediaTypeFile)]
    [InlineData(OutboundAttachmentKind.File, "a.silk", QQHttpApiClient.MediaTypeAudio)]
    [InlineData(OutboundAttachmentKind.File, "a.AMR", QQHttpApiClient.MediaTypeAudio)]
    public async Task SendAsync_ShouldUploadWithQQFileType_AndSendMediaMessage(
        OutboundAttachmentKind kind,
        string fileName,
        int expectedFileType)
    {
        // 群聊走 upload（file_type 按种类/扩展名）→ msg_type=7
        var (adapter, handler) = CreateAdapter();
        var file = CreateFile(fileName, 16);
        var message = Message(
            ConversationKind.Group,
            text: null,
            Attachment(kind, file, fileName));

        var result = await adapter.SendAsync(message);

        result.Delivered.Should().BeTrue();
        var upload = handler.Requests.Should().ContainSingle(r => r.IsUpload).Which;
        upload.Path.Should().Be("/v2/groups/g1/files");
        upload.Body.Should().Contain($"\"file_type\":{expectedFileType}");
        handler.Requests.Should().ContainSingle(r => r.IsMediaMessage)
            .Which.Body.Should().Contain("\"msg_type\":7");
    }

    [Fact]
    public async Task SendAsync_TextWithLegacyMediaMarkers_ShouldNotTriggerUpload()
    {
        // 旧文本媒体标记协议已删除：标记按普通文本发送，不产生任何媒体请求
        var (adapter, handler) = CreateAdapter();
        var message = Message(
            ConversationKind.Direct,
            text: "见 [Image: https://cdn.example.com/a.png] 以及 [File: https://cdn.example.com/d.pdf]");

        var result = await adapter.SendAsync(message);

        result.Delivered.Should().BeTrue();
        handler.Requests.Should().ContainSingle(r => r.IsTextMessage);
        handler.Requests.Count(r => r.IsUpload).Should().Be(0);
        handler.Requests.Count(r => r.IsMediaMessage).Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_ShouldAggregateFailure_WhenTextFailsButAttachmentSucceeds()
    {
        var (adapter, handler) = CreateAdapter();
        handler.OnRequest = (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            && !body.Contains("\"msg_type\":7", StringComparison.Ordinal)
                ? Json("""{"message":"bad text"}""", HttpStatusCode.BadRequest)
                : Json("""{"file_info":"fi-1"}""");

        var file = CreateFile("a.png", 16);
        var message = Message(
            ConversationKind.Direct,
            text: "说明",
            Attachment(OutboundAttachmentKind.Image, file, "a.png"));

        var result = await adapter.SendAsync(message);

        result.Delivered.Should().BeFalse();
        result.FailureKind.Should().Be(OutboundFailureKind.PlatformError);
        result.Detail.Should().Contain("1/2");
        handler.Requests.Should().ContainSingle(r => r.IsMediaMessage);
    }

    [Fact]
    public async Task SendParsedAsync_ShouldAcquirePassivePerReply()
    {
        var (adapter, handler) = CreateAdapter(tracker: CreateTracker(replyLimit: 1));
        var target = QQChannelAdapter.ToParsed(Target(ConversationKind.Direct, replyTo: "mid"));

        var first = await adapter.SendParsedAsync(target, "第一条");
        var second = await adapter.SendParsedAsync(target, "第二条");

        first.Delivered.Should().BeTrue();
        second.Delivered.Should().BeTrue();
        var bodies = handler.Requests.Where(r => r.IsTextMessage).Select(r => r.Body).ToList();
        bodies.Should().HaveCount(2);
        bodies[0].Should().Contain("\"msg_id\"");
        bodies[1].Should().NotContain("\"msg_id\"");
    }

    [Fact]
    public async Task SendParsedAsync_EmptyText_ShouldNotSendAnything()
    {
        var (adapter, handler) = CreateAdapter();
        var target = QQChannelAdapter.ToParsed(Target(ConversationKind.Direct, replyTo: "mid"));

        var result = await adapter.SendParsedAsync(target, "");

        result.Delivered.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("C2C_MESSAGE_CREATE", ConversationKind.Direct, "uopenid", "uopenid")]
    [InlineData("GROUP_AT_MESSAGE_CREATE", ConversationKind.Group, "mopenid", "gopenid")]
    [InlineData("AT_MESSAGE_CREATE", ConversationKind.Guild, "user1", "ch1")]
    [InlineData("DIRECT_MESSAGE_CREATE", ConversationKind.Direct, "user1", "user1")]
    public void TryMapInbound_ShouldFillChannelInboundMessage(
        string eventType,
        ConversationKind kind,
        string sender,
        string externalId)
    {
        using var doc = JsonDocument.Parse(Payload(eventType));
        var ok = QQChannelAdapter.TryMapInbound(
            eventType,
            doc.RootElement,
            new QQOptions(),
            new QQCommandInterceptor(),
            out var inbound);

        ok.Should().BeTrue();
        inbound!.Sender.ChannelId.Should().Be("qq");
        inbound.Sender.ExternalId.Should().Be(sender);
        inbound.Conversation.Kind.Should().Be(kind);
        inbound.Conversation.ChannelId.Should().Be("qq");
        inbound.Conversation.ExternalId.Should().Be(externalId);
        inbound.ReplyToMsgId.Should().Be("msg1");
        inbound.Text.Should().Be("hello");
        inbound.SessionId.Should().NotBeNullOrWhiteSpace();
        inbound.SessionId.Should().NotContain(":");
    }

    [Fact]
    public void TryMapInbound_Subscribe_ShouldSetCommandToken()
    {
        var json = """
        { "id": "msg1", "content": "/subscribe", "author": { "user_openid": "uopenid" } }
        """;
        using var doc = JsonDocument.Parse(json);
        QQChannelAdapter.TryMapInbound(
            "C2C_MESSAGE_CREATE",
            doc.RootElement,
            new QQOptions(),
            new QQCommandInterceptor(),
            out var inbound).Should().BeTrue();

        inbound!.CommandToken.Should().Be("subscribe");
    }

    [Fact]
    public void MapApiFailure_429_ShouldBeRateLimited()
    {
        var ex = new QQApiException(HttpStatusCode.TooManyRequests, "/v2/users/u/messages", "{}");
        QQFailureMapper.MapApiFailure(ex).Should().Be(OutboundFailureKind.RateLimited);
    }

    [Fact]
    public void MapApiFailure_Other4xx_ShouldBePlatformError()
    {
        var ex = new QQApiException(HttpStatusCode.BadRequest, "/v2/users/u/messages", "{}");
        QQFailureMapper.MapApiFailure(ex).Should().Be(OutboundFailureKind.PlatformError);
    }

    [Fact]
    public void ReplyCollector_LoopComplete_ShouldBeRunCompleted()
    {
        var collector = new GatewayAssistantReplyCollector();
        collector.Apply(new GatewayEvent
        {
            Object = GatewayEventObject.Response,
            Status = GatewayEventStatus.InProgress,
            SessionId = "qq_direct_u1",
            SourceType = GatewayAssistantReplyCollector.LoopStartSourceType
        });
        var disposition = collector.Apply(new GatewayEvent
        {
            Object = GatewayEventObject.Response,
            Status = GatewayEventStatus.Completed,
            SessionId = "qq_direct_u1",
            SourceType = GatewayAssistantReplyCollector.LoopCompleteSourceType
        });

        disposition.Should().Be(GatewayReplyDisposition.RunCompleted);
        collector.IsTerminal.Should().BeTrue();
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                File.Delete(file);
                var directory = Path.GetDirectoryName(file);
                if (directory != null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // 测试清理失败不影响断言
            }
        }
    }

    private static (QQChannelAdapter Adapter, CapturingHandler Handler) CreateAdapter(
        QQOptions? options = null,
        QQPassiveWindowTracker? tracker = null)
    {
        options ??= new QQOptions();
        var handler = new CapturingHandler();
        var factory = new StubHttpClientFactory(handler);
        var passiveWindow = tracker ?? CreateTracker(replyLimit: 5);
        var tokenProvider = new QQAccessTokenProvider(
            Options.Create(options), factory, NullLogger<QQAccessTokenProvider>.Instance);
        var http = new QQHttpApiClient(
            Options.Create(options), tokenProvider, factory, NullLogger<QQHttpApiClient>.Instance);
        var adapter = new QQChannelAdapter(
            http, passiveWindow, Options.Create(options), NullLogger<QQChannelAdapter>.Instance);
        return (adapter, handler);
    }

    private static QQPassiveWindowTracker CreateTracker(int replyLimit)
    {
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), replyLimit, () => T0);
        tracker.RegisterInbound("mid", T0);
        return tracker;
    }

    private string CreateFile(string name, int byteCount)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "seeing-qq-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[byteCount]);
        _tempFiles.Add(path);
        return path;
    }

    private static OutboundMessage Message(
        ConversationKind kind,
        string? text,
        params OutboundAttachment[] attachments) =>
        new()
        {
            Target = Target(kind, replyTo: "mid"),
            DeliveryKind = OutboundDeliveryKind.Reply,
            Content = new OutboundContent { Text = text, Attachments = attachments }
        };

    private static OutboundAttachment Attachment(OutboundAttachmentKind kind, string path, string? name = null) =>
        new()
        {
            Kind = kind,
            LocalPath = path,
            SizeBytes = new FileInfo(path).Length,
            FileName = name
        };

    private static DeliveryTarget Target(ConversationKind kind, string? replyTo, string? recipient = "member1")
    {
        var external = kind switch
        {
            ConversationKind.Group => "g1",
            ConversationKind.Guild => "c1",
            _ => "u1"
        };
        return new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = kind,
                ExternalId = external
            },
            RecipientUserId = recipient,
            ReplyToMsgId = replyTo
        };
    }

    private static string? OpenId(ParsedQQMessage parsed) => parsed.MessageType switch
    {
        "group" => parsed.GroupOpenId,
        "guild" => parsed.ChannelId,
        _ => parsed.SenderOpenId
    };

    private static string Payload(string eventType) => eventType switch
    {
        "GROUP_AT_MESSAGE_CREATE" => """
            { "id": "msg1", "content": "hello", "group_openid": "gopenid", "author": { "member_openid": "mopenid" } }
            """,
        "AT_MESSAGE_CREATE" => """
            { "id": "msg1", "content": "hello", "channel_id": "ch1", "guild_id": "g1", "author": { "id": "user1" } }
            """,
        "DIRECT_MESSAGE_CREATE" => """
            { "id": "msg1", "content": "hello", "guild_id": "g1", "author": { "id": "user1" } }
            """,
        _ => """
            { "id": "msg1", "content": "hello", "author": { "user_openid": "uopenid" } }
            """
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(Uri Uri, string Body)
    {
        public string Path => Uri.AbsolutePath;

        public bool IsUpload => Path.EndsWith("/files", StringComparison.Ordinal);

        public bool IsMessage => Path.EndsWith("/messages", StringComparison.Ordinal);

        public bool IsMediaMessage => IsMessage && Body.Contains("\"msg_type\":7", StringComparison.Ordinal);

        public bool IsTextMessage => IsMessage && !IsMediaMessage;
    }

    /// <summary>
    /// 抓取请求体的 HTTP handler。access_token 请求固定返回成功（不参与 <see cref="OnRequest"/>，
    /// 否则每个用例都要重复处理鉴权）；其余请求交 <see cref="OnRequest"/>，未设置时返回默认成功响应。
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage>? OnRequest { get; set; }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new CapturedRequest(request.RequestUri!, body));

            if (request.RequestUri!.AbsoluteUri.Contains("getAppAccessToken", StringComparison.Ordinal))
                return Json("""{"access_token":"token","expires_in":7200}""");

            return OnRequest?.Invoke(request, body) ?? Json("""{"file_info":"fi-default"}""");
        }
    }

    /// <summary>与真实 <see cref="IHttpClientFactory"/> 一致：每次返回新 <see cref="HttpClient"/>（共享 handler）。</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
