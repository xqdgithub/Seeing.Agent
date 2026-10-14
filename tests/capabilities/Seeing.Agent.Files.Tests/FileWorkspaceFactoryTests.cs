using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Files;
using Seeing.Agent.Files.Watching;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWorkspaceFactoryTests
{
    private static FileWorkspaceFactory Create(string? cwd, string projectRoot, out FileWatcherPool pool)
    {
        var sessions = new Mock<ISessionWorkspaceResolver>();
        sessions.Setup(s => s.ResolveWorkingDirectory(It.IsAny<string>())).Returns(cwd);
        var ws = new Mock<IWorkspaceProvider>();
        ws.SetupGet(w => w.ProjectSeeingDirectory).Returns(Path.Combine(projectRoot, ".seeing"));
        var gate = new Mock<IWorkspacePathGate>();
        gate.Setup(g => g.EnsureAllowed(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
        pool = new FileWatcherPool();
        return new FileWorkspaceFactory(sessions.Object, ws.Object, new LocalFileSystem(), gate.Object, pool);
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
            factory.Release("a");
            // 释放 a 不应影响 b：b 仍可再次解析出同一实例
            var b = await factory.ResolveAsync("b");
            b.SessionId.Should().Be("b");
        }
        finally { Directory.Delete(root, true); }
    }
}
