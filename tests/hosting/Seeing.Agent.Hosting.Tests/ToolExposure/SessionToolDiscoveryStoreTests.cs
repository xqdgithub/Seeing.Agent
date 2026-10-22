using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// 发现态写入端口：只写入"待生效"集合，不影响本轮视图；会话不可用时返回 false。
/// </summary>
public class SessionToolDiscoveryStoreTests
{
    private static (SessionToolDiscoveryStore Store, SessionData Session) Create()
    {
        var session = SessionData.Create();
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.Get(session.Id)).Returns(session);
        return (new SessionToolDiscoveryStore(manager.Object, NullLogger<SessionToolDiscoveryStore>.Instance), session);
    }

    [Fact]
    public void Add_ShouldWritePending_NotDiscovered()
    {
        var (store, session) = Create();

        store.Add(session.Id, ["tool_a", "tool_b"]).Should().BeTrue();

        // 写入的是"待生效"，本轮视图（Discovered）不变，下一轮才提升
        session.ToolExposure.GetDiscovered().Should().BeEmpty();
        session.ToolExposure.GetPending().Should().Equal("tool_a", "tool_b");
    }

    [Fact]
    public void Add_ShouldDeduplicateAgainstDiscoveredAndPending()
    {
        var (store, session) = Create();
        session.ToolExposure.AddPending(["tool_a"]);
        session.ToolExposure.PromotePending();
        store.Add(session.Id, ["tool_a", "tool_b"]);

        store.Add(session.Id, ["tool_b", "tool_c"]);

        session.ToolExposure.GetPending().Should().Equal("tool_b", "tool_c");
    }

    [Fact]
    public void Add_ShouldReturnFalse_WhenSessionNotCached()
    {
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.Get(It.IsAny<string>())).Returns((SessionData?)null);
        var store = new SessionToolDiscoveryStore(manager.Object, NullLogger<SessionToolDiscoveryStore>.Instance);

        store.Add("missing", ["tool_a"]).Should().BeFalse();
    }

    [Fact]
    public void PromotePending_ShouldMovePendingIntoDiscovered_KeepingOrder()
    {
        var (store, session) = Create();
        store.Add(session.Id, ["b", "c"]);

        session.ToolExposure.PromotePending();

        session.ToolExposure.GetDiscovered().Should().Equal("b", "c");
        session.ToolExposure.GetPending().Should().BeEmpty();
    }
}
