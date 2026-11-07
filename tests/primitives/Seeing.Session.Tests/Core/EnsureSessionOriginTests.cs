using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Session.Tests.Core;

/// <summary>EnsureSessionAsync 只在新建时写入来源；已有会话保持原来源。</summary>
public class EnsureSessionOriginTests
{
    [Fact]
    public async Task EnsureSession_NewSession_AppliesOrigin()
    {
        var manager = new SessionManager();

        var session = await manager.EnsureSessionAsync("hb_1", origin: SessionOrigin.Heartbeat);

        session.Origin.Should().Be(SessionOrigin.Heartbeat);
    }

    [Fact]
    public async Task EnsureSession_ExistingSession_KeepsOriginalOrigin()
    {
        var manager = new SessionManager();
        await manager.EnsureSessionAsync("u_1", origin: SessionOrigin.User);

        var again = await manager.EnsureSessionAsync("u_1", origin: SessionOrigin.Heartbeat);

        again.Origin.Should().Be(SessionOrigin.User, "已有会话的来源不应被后续调用改写");
    }

    [Fact]
    public async Task EnsureSession_WithoutOrigin_DefaultsToUser()
    {
        var manager = new SessionManager();

        var session = await manager.EnsureSessionAsync("plain");

        session.Origin.Should().Be(SessionOrigin.User);
    }

    [Fact]
    public async Task EnsureSession_ConcurrentFirstCreation_KeepsFirstOrigin()
    {
        var manager = new SessionManager();

        var tasks = new[]
        {
            manager.EnsureSessionAsync("c_1", origin: SessionOrigin.Scheduler),
            manager.EnsureSessionAsync("c_1", origin: SessionOrigin.Heartbeat)
        };
        await Task.WhenAll(tasks);

        (await manager.EnsureSessionAsync("c_1")).Origin.Should().Be(SessionOrigin.Scheduler,
            "并发去重下由先进入的那次创建参数生效");
    }
}
