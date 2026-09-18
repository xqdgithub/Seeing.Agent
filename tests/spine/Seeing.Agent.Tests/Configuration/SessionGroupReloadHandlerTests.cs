using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// Task 10：工作区切换时，会话组存储应重定位到新工作区的 .seeing/session-groups。
/// </summary>
public class SessionGroupReloadHandlerTests
{
    [Fact]
    public async Task ReloadAsync_WithNewWorkspace_ShouldRelocateStoreToWorkspaceSessionGroups()
    {
        var dirA = NewTempDir();
        var dirB = NewTempDir();
        try
        {
            var store = new FileSessionGroupStore(dirA);
            var handler = new SessionGroupReloadHandler(store, Mock.Of<ISessionGroupManager>());

            // 目录 A 写入一个组
            await store.SaveAsync(new SessionGroup { Id = "before", AnchorSessionId = "s-before" });

            // 触发工作区切换 A -> B
            await handler.ReloadAsync(new WorkspaceChange { NewWorkspace = dirB }, CancellationToken.None);

            var expected = Path.Combine(dirB, ".seeing", "session-groups");
            store.BaseDirectory.Should().Be(expected);

            // 重定位后写入落到 B，并可从 B 读到
            await store.SaveAsync(new SessionGroup { Id = "after", AnchorSessionId = "s-after" });
            File.Exists(Path.Combine(expected, "after.json")).Should().BeTrue();

            var loaded = await store.LoadAsync("after");
            loaded.Should().NotBeNull();
            loaded!.AnchorSessionId.Should().Be("s-after");

            // A 中的旧组不在 B
            (await store.LoadAsync("before")).Should().BeNull();
        }
        finally
        {
            Cleanup(dirA);
            Cleanup(dirB);
        }
    }

    [Fact]
    public async Task ReloadAsync_WithEmptyNewWorkspace_ShouldNotRelocate()
    {
        var dirA = NewTempDir();
        try
        {
            var store = new FileSessionGroupStore(dirA);
            var handler = new SessionGroupReloadHandler(store, Mock.Of<ISessionGroupManager>());

            await handler.ReloadAsync(new WorkspaceChange { NewWorkspace = null }, CancellationToken.None);

            store.BaseDirectory.Should().Be(dirA);
        }
        finally
        {
            Cleanup(dirA);
        }
    }

    [Fact]
    public void ComponentId_ShouldBe_SessionGroups()
    {
        var handler = new SessionGroupReloadHandler(Mock.Of<ISessionGroupStore>(), Mock.Of<ISessionGroupManager>());

        handler.ComponentId.Should().Be("session.groups");
        handler.ChangeTypes.Should().Contain(typeof(WorkspaceChange));
    }

    [Fact]
    public async Task ReloadAsync_WithNewWorkspace_ShouldClearGroupManagerCache()
    {
        var dirA = NewTempDir();
        var dirB = NewTempDir();
        try
        {
            var store = new FileSessionGroupStore(dirA);
            var groups = new Mock<ISessionGroupManager>();
            var handler = new SessionGroupReloadHandler(store, groups.Object);

            await handler.ReloadAsync(new WorkspaceChange { NewWorkspace = dirB }, CancellationToken.None);

            groups.Verify(g => g.ClearCache(), Times.Once);
        }
        finally
        {
            Cleanup(dirA);
            Cleanup(dirB);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-grp-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
