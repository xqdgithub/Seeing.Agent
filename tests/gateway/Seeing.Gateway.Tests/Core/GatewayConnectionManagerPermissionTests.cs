using System.Net.WebSockets;
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Core;
using Xunit;

namespace Seeing.Gateway.Tests.Core;

/// <summary>
/// G3：断连 → 对该连接订阅过的会话取消在途权限请求（Deny(Cancellation)）。
/// </summary>
public class GatewayConnectionManagerPermissionTests
{
    [Fact]
    public void Unregister_DisconnectedSubscriber_ShouldCancelPendingWithCancellation()
    {
        var manager = new Mock<IPermissionRequestManager>();
        manager.Setup(m => m.GetPending("ses_1")).Returns(
        [
            new PermissionRequest
            {
                RequestId = "req_1",
                SessionId = "ses_1",
                PermissionKind = "tool.execute"
            }
        ]);
        var connectionManager = new GatewayConnectionManager(manager.Object);
        var connection = connectionManager.Register(Mock.Of<WebSocket>());
        connectionManager.SubscribeSession(connection.ConnectionId, "ses_1");

        connectionManager.Unregister(connection.ConnectionId);

        manager.Verify(
            m => m.TryResolve(
                "req_1",
                PermissionEffect.Deny,
                PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation,
                "连接断开",
                "ses_1"),
            Times.Once);
    }

    [Fact]
    public void Unregister_ConnectionWithoutSubscriptions_ShouldNotResolve()
    {
        var manager = new Mock<IPermissionRequestManager>();
        var connectionManager = new GatewayConnectionManager(manager.Object);
        var connection = connectionManager.Register(Mock.Of<WebSocket>());

        connectionManager.Unregister(connection.ConnectionId);

        manager.Verify(
            m => m.TryResolve(
                It.IsAny<string>(),
                It.IsAny<PermissionEffect>(),
                It.IsAny<PermissionGrantScope>(),
                It.IsAny<PermissionResolvedBy>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public void Unregister_UnknownConnection_ShouldNotResolve()
    {
        var manager = new Mock<IPermissionRequestManager>();
        var connectionManager = new GatewayConnectionManager(manager.Object);

        var act = () => connectionManager.Unregister("unknown");

        act.Should().NotThrow();
        manager.Verify(
            m => m.TryResolve(
                It.IsAny<string>(),
                It.IsAny<PermissionEffect>(),
                It.IsAny<PermissionGrantScope>(),
                It.IsAny<PermissionResolvedBy>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Never);
    }
}
