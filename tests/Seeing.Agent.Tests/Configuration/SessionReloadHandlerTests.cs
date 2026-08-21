using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class SessionReloadHandlerTests
{
    [Fact]
    public async Task 工作区切换_可重定位存储换目录并清缓存()
    {
        var storeMock = new Mock<IRelocatableSessionStore>();
        var sessionManager = new SessionManager();
        var handler = new SessionReloadHandler(storeMock.Object, sessionManager);

        sessionManager.Create();
        sessionManager.List().Should().HaveCount(1);

        await handler.ReloadAsync(
            new WorkspaceChange { OldWorkspace = "/old", NewWorkspace = "/new" },
            CancellationToken.None);

        storeMock.Verify(x => x.SetBaseDirectory(Path.Combine("/new", ".seeing", "sessions")), Times.Once);
        sessionManager.List().Should().BeEmpty();
    }

    [Fact]
    public async Task 工作区切换_存储不可重定位_仅清缓存()
    {
        var storeMock = new Mock<ISessionStore>();
        var sessionManager = new SessionManager();
        var handler = new SessionReloadHandler(storeMock.Object, sessionManager);

        sessionManager.Create();

        await handler.ReloadAsync(
            new WorkspaceChange { OldWorkspace = "/old", NewWorkspace = "/new" },
            CancellationToken.None);

        sessionManager.List().Should().BeEmpty();
    }

    [Fact]
    public void ChangeTypes_声明订阅WorkspaceChange()
    {
        var handler = new SessionReloadHandler(
            new Mock<ISessionStore>().Object,
            new SessionManager());

        handler.ChangeTypes.Should().Contain(typeof(WorkspaceChange));
    }

    [Fact]
    public void ComponentId_为session()
    {
        var handler = new SessionReloadHandler(
            new Mock<ISessionStore>().Object,
            new SessionManager());

        handler.ComponentId.Should().Be("session");
    }
}
