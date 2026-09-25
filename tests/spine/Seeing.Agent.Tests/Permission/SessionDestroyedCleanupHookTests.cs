using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 会话销毁清理（P1-16）：订阅 session.destroyed，收敛该会话在途审批并清除授权记忆/白名单目录。
/// </summary>
public class SessionDestroyedCleanupHookTests
{
    private static SessionDestroyedCleanupHook CreateHook(
        Mock<IPermissionRequestManager> requests, PermissionGrantStore grants) =>
        new(requests.Object, grants, NullLogger<SessionDestroyedCleanupHook>.Instance);

    private static HookPayload Payload(string sessionId, SessionData? session = null)
    {
        var result = session is null
            ? null
            : new Dictionary<string, object?> { ["session"] = session };
        return HookPayload.FireAndForget(HookRegistry.SessionDestroyed, sessionId, result: result);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDenyPendingAndClearGrants()
    {
        var sessions = new Mock<IPermissionRequestManager>();
        sessions.Setup(m => m.GetPending("s1"))
            .Returns(new[]
            {
                new PermissionRequest { RequestId = "r1", SessionId = "s1", PermissionKind = "tool.execute" },
                new PermissionRequest { RequestId = "r2", SessionId = "s1", PermissionKind = "shell.execute" }
            });
        sessions.Setup(m => m.TryResolve(
                It.IsAny<string>(), PermissionEffect.Deny, PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation, It.IsAny<string>(), "s1"))
            .Returns(true);

        var grants = new PermissionGrantStore();
        grants.Add("s1", new PermissionGrant("tool.execute", "res", PermissionGrantScope.Session, PermissionEffect.Allow));
        var dir = Path.Combine(Path.GetTempPath(), "seeing-cleanup-" + Guid.NewGuid().ToString("N"));
        grants.AddSessionDirectory("s1", dir);

        var hook = CreateHook(sessions, grants);

        var result = await hook.ExecuteAsync(Payload("s1"));

        result.Continue.Should().BeTrue();
        sessions.Verify(m => m.TryResolve(
            "r1", PermissionEffect.Deny, PermissionGrantScope.Once,
            PermissionResolvedBy.Cancellation, It.IsAny<string>(), "s1"), Times.Once);
        sessions.Verify(m => m.TryResolve(
            "r2", PermissionEffect.Deny, PermissionGrantScope.Once,
            PermissionResolvedBy.Cancellation, It.IsAny<string>(), "s1"), Times.Once);

        grants.Lookup("s1", "tool.execute", "res").Should().BeEmpty("授权记忆须清除");
        grants.ContainsSessionPath("s1", Path.Combine(dir, "child.txt")).Should().BeFalse("白名单目录须清除");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResolveSessionIdFromPayloadResult()
    {
        var sessions = new Mock<IPermissionRequestManager>();
        sessions.Setup(m => m.GetPending("s1"))
            .Returns(new[] { new PermissionRequest { RequestId = "r1", SessionId = "s1", PermissionKind = "tool.execute" } });

        var grants = new PermissionGrantStore();
        grants.Add("s1", new PermissionGrant("tool.execute", "res", PermissionGrantScope.Session, PermissionEffect.Allow));

        var hook = CreateHook(sessions, grants);
        var session = new SessionData { Id = "s1" };

        // SessionId 为空时从 result["session"] 解析
        await hook.ExecuteAsync(Payload(string.Empty, session));

        sessions.Verify(m => m.GetPending("s1"), Times.Once);
        sessions.Verify(m => m.TryResolve(
            "r1", PermissionEffect.Deny, PermissionGrantScope.Once,
            PermissionResolvedBy.Cancellation, It.IsAny<string>(), "s1"), Times.Once);
        grants.Lookup("s1", "tool.execute", "res").Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoSessionId_ShouldNoOp()
    {
        var sessions = new Mock<IPermissionRequestManager>();
        var grants = new PermissionGrantStore();
        grants.Add("s1", new PermissionGrant("tool.execute", "res", PermissionGrantScope.Session, PermissionEffect.Allow));

        var hook = CreateHook(sessions, grants);

        await hook.ExecuteAsync(Payload(string.Empty));

        sessions.Verify(m => m.GetPending(It.IsAny<string>()), Times.Never);
        grants.Lookup("s1", "tool.execute", "res").Should().NotBeEmpty();
    }
}
