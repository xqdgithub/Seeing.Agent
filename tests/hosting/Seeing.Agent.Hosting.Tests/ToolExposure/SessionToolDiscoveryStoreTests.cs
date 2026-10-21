using FluentAssertions;
using Moq;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class SessionToolDiscoveryStoreTests
{
    [Fact]
    public void Add_AfterRepeatDiscovery_ShouldBeMonotonic()
    {
        // Arrange
        var (session, store) = CreateStore();

        // Act：同一批工具发现两次
        store.Add(session.Id, new[] { "tool_a", "tool_b" });
        store.Add(session.Id, new[] { "tool_a", "tool_b", "tool_c" });

        // Assert：不重复、不丢项、按发现顺序
        var discovered = store.Get(session.Id);
        discovered.Should().Contain(new[] { "tool_a", "tool_b", "tool_c" });
        session.DiscoveredTools.Should().Equal("tool_a", "tool_b", "tool_c");
        session.DiscoveredTools.Should().HaveCount(3, "重复 Add 不得产生重复条目");
    }

    [Fact]
    public void Prune_WhenToolsVanished_ShouldDropVanishedIds()
    {
        // Arrange
        var (session, store) = CreateStore();
        store.Add(session.Id, new[] { "tool_a", "tool_b", "tool_c" });

        // Act：tool_b 消失
        var available = new HashSet<string>(
            new[] { "tool_a", "tool_c" }, StringComparer.OrdinalIgnoreCase);
        store.Prune(session.Id, available);

        // Assert：仅去掉不在可用集的 ID，剩余保持发现顺序
        session.DiscoveredTools.Should().Equal("tool_a", "tool_c");
        store.Get(session.Id).Should().BeEquivalentTo(new[] { "tool_a", "tool_c" });
    }

    [Fact]
    public void Add_WhenLargeBatch_ShouldNotEvictOnSize()
    {
        // Arrange
        var (session, store) = CreateStore();
        var many = Enumerable.Range(0, 5000).Select(i => $"tool_{i}");

        // Act
        store.Add(session.Id, many);

        // Assert：无上限淘汰，全部保留
        session.DiscoveredTools.Should().HaveCount(5000);
        store.Get(session.Id).Should().HaveCount(5000);
        store.Get(session.Id).Should().Contain("tool_4999");
    }

    [Fact]
    public void Get_WhenSessionMissing_ShouldReturnEmptySet()
    {
        // Arrange：管理器缓存取不到会话
        var managerMock = new Mock<ISessionManager>();
        managerMock.Setup(m => m.Get(It.IsAny<string>())).Returns((SessionData?)null);
        var store = new SessionToolDiscoveryStore(managerMock.Object);

        // Act & Assert
        store.Get("ses_missing").Should().BeEmpty();

        // Add/Prune 对缺失会话不抛
        var act = () =>
        {
            store.Add("ses_missing", new[] { "tool_a" });
            store.Prune("ses_missing", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        };
        act.Should().NotThrow();
    }

    private static (SessionData Session, SessionToolDiscoveryStore Store) CreateStore()
    {
        var session = SessionData.Create();
        var managerMock = new Mock<ISessionManager>();
        managerMock.Setup(m => m.Get(session.Id)).Returns(session);
        var store = new SessionToolDiscoveryStore(managerMock.Object);
        return (session, store);
    }
}
