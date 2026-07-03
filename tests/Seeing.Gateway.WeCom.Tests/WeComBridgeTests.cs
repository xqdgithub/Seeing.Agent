using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComSessionResolverTests
{
    [Fact]
    public void ResolveSessionId_SingleChat_ShouldUseUserId()
    {
        var message = new ParsedWeComMessage
        {
            Frame = new WeComWsFrame(),
            UserId = "user_001",
            ChatId = "chat_001",
            ChatType = "single",
            MessageId = "msg_1",
            InputParts = [new GatewayTextContentPart("hello")]
        };

        var sessionId = WeComSessionResolver.ResolveSessionId(message, new WeComOptions());

        sessionId.Should().Be("wecom_user_001");
    }

    [Fact]
    public void ResolveSessionId_SingleChat_ShouldSanitizeInvalidFileNameChars()
    {
        var message = new ParsedWeComMessage
        {
            Frame = new WeComWsFrame(),
            UserId = "15706403286",
            ChatId = "chat_001",
            ChatType = "single",
            MessageId = "msg_1",
            InputParts = [new GatewayTextContentPart("hello")]
        };

        var sessionId = WeComSessionResolver.ResolveSessionId(message, new WeComOptions());

        sessionId.Should().Be("wecom_15706403286");
    }

    [Fact]
    public void ResolveSessionId_GroupChat_ShouldUseGroupPrefix()
    {
        var message = new ParsedWeComMessage
        {
            Frame = new WeComWsFrame(),
            UserId = "user_001",
            ChatId = "group_chat_001",
            ChatType = "group",
            MessageId = "msg_1",
            InputParts = [new GatewayTextContentPart("hello")]
        };

        var sessionId = WeComSessionResolver.ResolveSessionId(
            message,
            new WeComOptions { ShareSessionInGroup = true });

        sessionId.Should().Be("wecom_group_group_chat_001");
    }
}

public class WeComMessageParserTests
{
    [Fact]
    public void TryParseText_ShouldExtractTextMessage()
    {
        var context = new WeComIncomingContext
        {
            Frame = new WeComWsFrame { Headers = new WeComWsHeaders { ReqId = "req_1" } },
            Message = new WeComIncomingMessage
            {
                MsgId = "msg_1",
                MsgType = "text",
                From = new WeComIncomingFrom { UserId = "user_001" },
                ChatId = "chat_1",
                ChatType = "single",
                Text = new WeComTextPayload { Content = "  hello world  " }
            }
        };

        var ok = WeComMessageParser.TryParseText(context, out var parsed);

        ok.Should().BeTrue();
        parsed!.Text.Should().Be("hello world");
        parsed.UserId.Should().Be("user_001");
    }

    [Fact]
    public void ToGatewayRequest_ShouldBuildWeComRequest()
    {
        var parsed = new ParsedWeComMessage
        {
            Frame = new WeComWsFrame(),
            UserId = "user_001",
            ChatId = "chat_1",
            ChatType = "single",
            MessageId = "msg_1",
            InputParts = [new GatewayTextContentPart("hello")]
        };

        var request = WeComMessageParser.ToGatewayRequest(parsed, "wecom_user_001");

        request.SessionId.Should().Be("wecom_user_001");
        request.ChannelId.Should().Be("wecom");
        request.AgentId.Should().BeNull();
        request.Input.Should().ContainSingle(p => p is GatewayTextContentPart);
    }

    [Fact]
    public void ToGatewayRequest_ShouldPassConfiguredAgentAndModel()
    {
        var parsed = new ParsedWeComMessage
        {
            Frame = new WeComWsFrame(),
            UserId = "user_001",
            ChatId = "chat_1",
            ChatType = "single",
            MessageId = "msg_1",
            InputParts = [new GatewayTextContentPart("hello")]
        };

        var request = WeComMessageParser.ToGatewayRequest(parsed, "wecom_user_001", "build", "gpt-4");

        request.AgentId.Should().Be("build");
        request.ModelId.Should().Be("gpt-4");
    }

    [Fact]
    public async Task TryParseAsync_VoiceWithTranscription_ShouldUseTextPart()
    {
        var context = new WeComIncomingContext
        {
            Frame = new WeComWsFrame(),
            Message = new WeComIncomingMessage
            {
                MsgId = "msg_voice",
                MsgType = "voice",
                From = new WeComIncomingFrom { UserId = "user_001" },
                Voice = new WeComVoicePayload { Content = "  你好  " }
            }
        };

        var fetcher = CreateMediaFetcher();
        var (ok, parsed) = await WeComMessageParser.TryParseAsync(context, fetcher);

        ok.Should().BeTrue();
        parsed!.InputParts.Should().ContainSingle(p => p is GatewayTextContentPart);
        parsed.Text.Should().Be("你好");
    }

    [Fact]
    public async Task TryParseAsync_VoiceWithoutTranscription_ShouldReturnUnsupportedReply()
    {
        var context = new WeComIncomingContext
        {
            Frame = new WeComWsFrame(),
            Message = new WeComIncomingMessage
            {
                MsgId = "msg_voice",
                MsgType = "voice",
                From = new WeComIncomingFrom { UserId = "user_001" },
                Voice = new WeComVoicePayload()
            }
        };

        var fetcher = CreateMediaFetcher();
        var (ok, parsed) = await WeComMessageParser.TryParseAsync(context, fetcher);

        ok.Should().BeTrue();
        parsed!.HasUnsupportedReply.Should().BeTrue();
        parsed.UnsupportedReply.Should().Contain("语音");
    }

    [Fact]
    public async Task TryParseAsync_UnsupportedType_ShouldReturnFriendlyReply()
    {
        var context = new WeComIncomingContext
        {
            Frame = new WeComWsFrame(),
            Message = new WeComIncomingMessage
            {
                MsgId = "msg_loc",
                MsgType = "location",
                From = new WeComIncomingFrom { UserId = "user_001" }
            }
        };

        var fetcher = CreateMediaFetcher();
        var (ok, parsed) = await WeComMessageParser.TryParseAsync(context, fetcher);

        ok.Should().BeTrue();
        parsed!.HasUnsupportedReply.Should().BeTrue();
        parsed.UnsupportedReply.Should().Contain("location");
    }

    private static WeComMediaFetcher CreateMediaFetcher()
    {
        var httpClient = new HttpClient();
        return new WeComMediaFetcher(
            httpClient,
            Options.Create(new WeComOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WeComMediaFetcher>.Instance);
    }
}

public class WeComEventParserTests
{
    [Fact]
    public void TryParseEnterChat_ShouldParseEnterChatEvent()
    {
        var frame = CreateEventFrame("enter_chat");

        var ok = WeComEventParser.TryParseEnterChat(frame, out var parsed);

        ok.Should().BeTrue();
        parsed!.UserId.Should().Be("user_001");
        parsed.ChatType.Should().Be("single");
    }

    [Fact]
    public void TryParseTemplateCardEvent_ShouldParseButtonClick()
    {
        var frame = CreateEventFrame("template_card_event", taskId: "perm_abc", eventKey: "allow");

        var ok = WeComEventParser.TryParseTemplateCardEvent(frame, out var parsed);

        ok.Should().BeTrue();
        parsed!.TaskId.Should().Be("perm_abc");
        parsed.EventKey.Should().Be("allow");
    }

    [Fact]
    public void TryParseEnterChat_ShouldRejectOtherEventTypes()
    {
        var frame = CreateEventFrame("template_card_event", taskId: "perm_abc", eventKey: "allow");

        WeComEventParser.TryParseEnterChat(frame, out _).Should().BeFalse();
    }

    private static WeComWsFrame CreateEventFrame(
        string eventType,
        string? taskId = null,
        string? eventKey = null)
    {
        var message = new WeComIncomingMessage
        {
            MsgId = "evt_1",
            MsgType = "event",
            From = new WeComIncomingFrom { UserId = "user_001" },
            ChatId = "chat_1",
            ChatType = "single",
            Event = new WeComEventPayload
            {
                EventType = eventType,
                TemplateCardEvent = taskId == null
                    ? null
                    : new WeComTemplateCardEventPayload
                    {
                        TaskId = taskId,
                        EventKey = eventKey,
                        CardType = "button_interaction"
                    }
            }
        };

        return new WeComWsFrame
        {
            Cmd = WeComWsCommands.EventCallback,
            Headers = new WeComWsHeaders { ReqId = "req_evt_1" },
            Body = System.Text.Json.JsonSerializer.SerializeToElement(message, WeComWsJson.Options)
        };
    }
}

public class WeComPermissionPolicyTests
{
    [Fact]
    public void ShouldPromptUser_HighRisk_ShouldReturnTrue()
    {
        var policy = CreatePolicy(new WeComOptions { AutoApproveLowRisk = true });
        var evt = CreatePermissionEvent(riskLevel: "high");

        policy.ShouldPromptUser(evt).Should().BeTrue();
    }

    [Fact]
    public void ShouldPromptUser_LowRisk_ShouldReturnFalse()
    {
        var policy = CreatePolicy(new WeComOptions { AutoApproveLowRisk = true });
        var evt = CreatePermissionEvent(riskLevel: "low", kind: "read");

        policy.ShouldPromptUser(evt).Should().BeFalse();
    }

    [Fact]
    public void ShouldPromptUser_ShellKind_ShouldReturnTrue()
    {
        var policy = CreatePolicy(new WeComOptions { AutoApproveLowRisk = true });
        var evt = CreatePermissionEvent(riskLevel: "low", kind: "shell");

        policy.ShouldPromptUser(evt).Should().BeTrue();
    }

    [Fact]
    public void ShouldPromptUser_AutoApproveDisabled_ShouldAlwaysPrompt()
    {
        var policy = CreatePolicy(new WeComOptions { AutoApproveLowRisk = false });
        var evt = CreatePermissionEvent(riskLevel: "low", kind: "read");

        policy.ShouldPromptUser(evt).Should().BeTrue();
    }

    [Fact]
    public void EventKeyMapping_ShouldRecognizeAllowAndDeny()
    {
        var policy = CreatePolicy(new WeComOptions());

        policy.IsAllowEventKey("allow").Should().BeTrue();
        policy.IsDenyEventKey("deny").Should().BeTrue();
        policy.IsAllowEventKey("unknown").Should().BeFalse();
    }

    private static WeComPermissionPolicy CreatePolicy(WeComOptions options)
        => new(Options.Create(options));

    private static GatewayEvent CreatePermissionEvent(string riskLevel, string kind = "file")
        => new()
        {
            Object = GatewayEventObject.Permission,
            Status = GatewayEventStatus.InProgress,
            SessionId = "wecom_user_001",
            Data = new GatewayEventData
            {
                PermissionId = "perm_1",
                RiskLevel = riskLevel,
                PermissionKind = kind,
                Resource = "/tmp/test.txt"
            }
        };
}

public class WeComMediaDecryptorTests
{
    [Fact]
    public void Decrypt_WithBase64AesKey_ShouldRestorePlaintext()
    {
        var plaintext = Encoding.UTF8.GetBytes("Hello WeCom Media");
        var keyMaterial = new byte[32];
        RandomNumberGenerator.Fill(keyMaterial);
        var aesKeyBase64 = Convert.ToBase64String(keyMaterial);

        var key = keyMaterial.AsSpan(0, 32).ToArray();
        var iv = keyMaterial.AsSpan(0, 16).ToArray();

        byte[] encrypted;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        var decrypted = WeComMediaDecryptor.Decrypt(encrypted, aesKeyBase64);

        decrypted.Should().Equal(plaintext);
    }
}

public class WeComPermissionCardBuilderTests
{
    [Fact]
    public void BuildPromptCard_ShouldIncludeAllowAndDenyButtons()
    {
        var card = WeComPermissionCardBuilder.BuildPromptCard(
            "perm_task_1",
            "权限确认",
            "执行 shell 命令",
            "cmd.exe /c dir");

        card.MsgType.Should().Be("template_card");
        card.TemplateCard.TaskId.Should().Be("perm_task_1");
        card.TemplateCard.ButtonList.Should().HaveCount(2);
        card.TemplateCard.ButtonList![0].Key.Should().Be("allow");
        card.TemplateCard.ButtonList[1].Key.Should().Be("deny");
    }

    [Fact]
    public void BuildResultCard_ShouldSetApprovedTitle()
    {
        var card = WeComPermissionCardBuilder.BuildResultCard("perm_task_1", allowed: true);

        card.ResponseType.Should().Be("update_template_card");
        card.TemplateCard.MainTitle!.Title.Should().Be("已批准");
    }
}
