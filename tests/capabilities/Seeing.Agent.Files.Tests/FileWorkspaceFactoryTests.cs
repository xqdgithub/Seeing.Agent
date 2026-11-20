using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Files.Watching;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWorkspaceFactoryTests
{
    private sealed class TestRootSet(params string[] roots) : Seeing.Agent.Abstractions.Directories.ISessionRootSet
    {
        public IReadOnlyList<Seeing.Agent.Abstractions.Directories.SessionRoot> Roots(string sessionId)
            => roots.Select((root, index) => new Seeing.Agent.Abstractions.Directories.SessionRoot(
                Path.GetFullPath(root),
                index == 0
                    ? Seeing.Agent.Abstractions.Directories.SessionRootKind.Project
                    : Seeing.Agent.Abstractions.Directories.SessionRootKind.Collaboration,
                $"root{index}",
                Directory.Exists(root))).ToArray();

        public bool IsAuthorized(string sessionId, string absolutePath) => true;
        public bool IsTrusted(string sessionId, string absolutePath) => true;
    }

    private static FileWorkspaceFactory CreateMulti(string[] roots, out FileWatcherPool pool)
    {
        var gate = new Mock<IWorkspacePathGate>();
        gate.Setup(g => g.EnsureAllowed(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
        pool = new FileWatcherPool();
        return new FileWorkspaceFactory(
            new TestRootSet(roots), new LocalFileSystem(), gate.Object, pool, new ProcessPathWriteLock());
    }

    private static FileWorkspaceFactory Create(string? cwd, string projectRoot, out FileWatcherPool pool)
    {
        var gate = new Mock<IWorkspacePathGate>();
        gate.Setup(g => g.EnsureAllowed(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
        pool = new FileWatcherPool();
        return new FileWorkspaceFactory(
            new TestRootSet(cwd ?? projectRoot),
            new LocalFileSystem(),
            gate.Object,
            pool,
            new ProcessPathWriteLock());
    }

    [Fact]
    public async Task ListAsync_MultipleRoots_ShouldReturnAllAndRouteByLongestPrefix()
    {
        var project = Path.Combine(Path.GetTempPath(), "fwr_p_" + Guid.NewGuid().ToString("N"));
        var collab = Path.Combine(Path.GetTempPath(), "fwr_c_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(collab);
        try
        {
            var factory = CreateMulti([project, collab], out _);

            var list = await factory.ListAsync("s1");
            list.Should().HaveCount(2);
            list[0].Root.Kind.Should().Be(Seeing.Agent.Abstractions.Files.FileWorkspaceRootKind.Project);
            list[1].Root.Kind.Should().Be(Seeing.Agent.Abstractions.Files.FileWorkspaceRootKind.Collaboration);

            var routed = await factory.ResolveAsync("s1", Path.Combine(collab, "a.txt"));
            routed!.RootPath.Should().Be(Path.GetFullPath(collab));

            var exact = await factory.GetAsync("s1", list[1].RootId);
            exact.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(project, true);
            Directory.Delete(collab, true);
        }
    }

    [Fact]
    public async Task Resolve_SameSessionSameRoot_ShouldReuseInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "fwf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = Create(root, root, out _);
            var a = await factory.ResolveAsync("s1");
            var b = await factory.ResolveAsync("s1");
            b.Should().BeSameAs(a);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Resolve_NoCwd_ShouldFallBackToProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fwf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = Create(cwd: null, projectRoot: root, out _);
            var ws = await factory.ResolveAsync("s1");
            ws.RootPath.Should().Be(Path.GetFullPath(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Release_TwiceOnSingleRef_ShouldNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "fwf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = Create(root, root, out _);
            await factory.ResolveAsync("s1");
            factory.Release("s1");
            var act = () => factory.Release("s1");
            act.Should().NotThrow();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Resolve_DifferentSessionsSameRoot_ShouldNotDisposeSharedWatcherOnOneRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "fwf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = Create(root, root, out var pool);
            await factory.ResolveAsync("a");
            await factory.ResolveAsync("b");

            // 模拟两个会话各自的变更订阅（各自 Acquire 一次池引用）
            var subA = pool.Subscribe(root);
            var subB = pool.Subscribe(root);
            pool.RootCount.Should().Be(1);

            // 释放 a（factory 引用 + 订阅）不应 dispose 共享 watcher
            factory.Release("a");
            subA.Dispose();
            pool.RootCount.Should().Be(1);

            // b 仍可再次解析出同一实例且订阅可用
            var b2 = await factory.ResolveAsync("b");
            b2.SessionId.Should().Be("b");

            subB.Dispose();
            factory.Release("b");
            pool.RootCount.Should().Be(0);
        }
        finally { Directory.Delete(root, true); }
    }
}
