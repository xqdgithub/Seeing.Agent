using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComSessionTrackerTests : IDisposable
{
    private readonly string _stateFile;
    private readonly List<string> _cleanupPaths = [];

    public WeComSessionTrackerTests()
    {
        _stateFile = Path.Combine(Path.GetTempPath(), $"wecom-sessions-{Guid.NewGuid():N}.json");
        _cleanupPaths.Add(_stateFile);
    }

    [Fact]
    public void ResolveSessionId_ShouldUseConversationKeyInitially()
    {
        var tracker = CreateTracker(new WeComOptions { SessionIdleTimeoutMinutes = 30 });
        var message = CreateMessage("user_001");

        tracker.ResolveSessionId(message).Should().Be("wecom_user_001");
    }

    [Fact]
    public void ResolveSessionId_ShouldRotateWhenIdle()
    {
        var tracker = CreateTracker(new WeComOptions { SessionIdleTimeoutMinutes = 30 });
        var message = CreateMessage("user_001");

        var first = tracker.ResolveSessionId(message);
        first.Should().Be("wecom_user_001");

        var entries = tracker.GetEntriesForTesting();
        entries["wecom_user_001"].LastActiveAtUtc = DateTime.UtcNow.AddMinutes(-31);

        var second = tracker.ResolveSessionId(message);
        second.Should().StartWith("wecom_user_001_");
        second.Should().NotBe(first);
    }

    [Fact]
    public void RotateSession_ShouldGenerateNewSessionId()
    {
        var tracker = CreateTracker(new WeComOptions { SessionIdleTimeoutMinutes = 30 });
        var message = CreateMessage("user_001");

        tracker.ResolveSessionId(message);
        var rotated = tracker.RotateSession(message, reason: "command_new");

        rotated.Should().StartWith("wecom_user_001_");
        tracker.ResolveSessionId(message).Should().Be(rotated);
    }

    [Fact]
    public void ResolveSessionId_ShouldNotRotateWhenTimeoutDisabled()
    {
        var tracker = CreateTracker(new WeComOptions { SessionIdleTimeoutMinutes = 0 });
        var message = CreateMessage("user_001");

        tracker.ResolveSessionId(message);
        tracker.GetEntriesForTesting()["wecom_user_001"].LastActiveAtUtc = DateTime.UtcNow.AddHours(-2);

        tracker.ResolveSessionId(message).Should().Be("wecom_user_001");
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private WeComSessionTracker CreateTracker(WeComOptions options)
    {
        options.SessionStateFile = _stateFile;
        return new WeComSessionTracker(
            Options.Create(options),
            NullLogger<WeComSessionTracker>.Instance);
    }

    private static ParsedWeComMessage CreateMessage(string userId) =>
        new()
        {
            Frame = new WeComWsFrame(),
            UserId = userId,
            ChatId = "chat_1",
            ChatType = "single",
            MessageId = Guid.NewGuid().ToString("N"),
            InputParts = [new GatewayTextContentPart("hello")]
        };
}

public class WeComCommandInterceptorTests
{
    [Fact]
    public void TryParseCommand_ShouldDetectClearAndNew()
    {
        var clear = CreateMessage("user", "/clear");
        WeComCommandInterceptor.TryParseCommand(clear, out var clearCmd).Should().BeTrue();
        clearCmd.Should().Be(WeComConversationCommand.Clear);

        var newer = CreateMessage("user", "/new");
        WeComCommandInterceptor.TryParseCommand(newer, out var newCmd).Should().BeTrue();
        newCmd.Should().Be(WeComConversationCommand.New);
    }

    [Fact]
    public void ExtractCommandText_GroupChat_ShouldStripMentionBeforeSlashCommand()
    {
        var message = CreateMessage("user", "@Bot /clear", chatType: "group");
        WeComCommandInterceptor.ExtractCommandText(message).Should().Be("/clear");
    }

    [Fact]
    public void ExtractCommandText_GroupChat_ShouldPreserveNormalAtMention()
    {
        var message = CreateMessage("user", "@Bot 你好", chatType: "group");
        WeComCommandInterceptor.ExtractCommandText(message).Should().Be("@Bot 你好");
    }

    private static ParsedWeComMessage CreateMessage(string userId, string text, string chatType = "single") =>
        new()
        {
            Frame = new WeComWsFrame(),
            UserId = userId,
            ChatId = "chat_1",
            ChatType = chatType,
            MessageId = Guid.NewGuid().ToString("N"),
            InputParts = [new GatewayTextContentPart(text)]
        };
}

public class WeComSessionResolverRotationTests
{
    [Fact]
    public void GenerateRotatedSessionId_ShouldAppendTimestampSuffix()
    {
        var rotated = WeComSessionResolver.GenerateRotatedSessionId("wecom_user_001");
        rotated.Should().MatchRegex(@"^wecom_user_001_\d{14}$");
    }

    [Fact]
    public void ResolveConversationKey_EnterChat_ShouldMatchMessageMapping()
    {
        var enterChat = new ParsedWeComEnterChat
        {
            Frame = new WeComWsFrame(),
            MessageId = "evt_1",
            UserId = "user_001",
            ChatId = "chat_1",
            ChatType = "single"
        };

        WeComSessionResolver.ResolveConversationKey(enterChat, new WeComOptions())
            .Should().Be("wecom_user_001");
    }
}
