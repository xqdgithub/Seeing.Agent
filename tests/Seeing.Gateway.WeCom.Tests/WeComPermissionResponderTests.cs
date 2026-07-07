using FluentAssertions;
using Seeing.Gateway.WeCom;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComPermissionResponderTests
{
    [Theory]
    [InlineData("批准", true)]
    [InlineData("allow", true)]
    [InlineData("拒绝", false)]
    [InlineData("deny", false)]
    public void TryParsePermissionReply_ShouldRecognizeKeywords(string text, bool expectedAllow)
    {
        var ok = WeComPermissionResponder.TryParsePermissionReply(text, out var allow);

        ok.Should().BeTrue();
        allow.Should().Be(expectedAllow);
    }

    [Fact]
    public void TryParsePermissionReply_SlashCommand_ShouldReturnFalse()
    {
        WeComPermissionResponder.TryParsePermissionReply("/clear", out _).Should().BeFalse();
    }
}

public class WeComPermissionStateTests
{
    [Fact]
    public void TryGetLatestPendingForSession_ShouldReturnMostRecent()
    {
        var state = new WeComPermissionState();
        state.Register(new PendingPermissionCard
        {
            SessionId = "sess_1",
            PermissionId = "perm_old",
            Resource = "a",
            PermissionKind = "tool",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        state.Register(new PendingPermissionCard
        {
            SessionId = "sess_1",
            PermissionId = "perm_new",
            Resource = "b",
            PermissionKind = "tool",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            RegisteredAt = DateTimeOffset.UtcNow
        });

        state.TryGetLatestPendingForSession("sess_1", out var pending).Should().BeTrue();
        pending.PermissionId.Should().Be("perm_new");
    }
}
