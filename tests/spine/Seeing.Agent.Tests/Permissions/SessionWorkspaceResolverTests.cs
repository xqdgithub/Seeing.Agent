using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permissions;

public class SessionWorkspaceResolverTests
{
    [Fact]
    public void ResolveWorkingDirectory_NoSession_ShouldReturnNull()
    {
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get("missing")).Returns((SessionData?)null);
        var resolver = new SessionWorkspaceResolver(sessions.Object);
        resolver.ResolveWorkingDirectory("missing").Should().BeNull();
    }

    [Fact]
    public void ResolveWorkingDirectory_EmptyId_ShouldReturnNull()
    {
        var resolver = new SessionWorkspaceResolver(new Mock<ISessionManager>().Object);
        resolver.ResolveWorkingDirectory("").Should().BeNull();
    }

    [Fact]
    public void ResolveWorkingDirectory_NonExistentDir_ShouldReturnNull()
    {
        var session = new SessionData { WorkingDirectory = Path.Combine(Path.GetTempPath(), "no-such-dir-xyz-123") };
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get("s1")).Returns(session);
        var resolver = new SessionWorkspaceResolver(sessions.Object);
        resolver.ResolveWorkingDirectory("s1").Should().BeNull();
    }

    [Fact]
    public void ResolveWorkingDirectory_ExistingDir_ShouldReturnFullPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var session = new SessionData { WorkingDirectory = dir };
            var sessions = new Mock<ISessionManager>();
            sessions.Setup(s => s.Get("s2")).Returns(session);
            var resolver = new SessionWorkspaceResolver(sessions.Object);
            resolver.ResolveWorkingDirectory("s2").Should().Be(Path.GetFullPath(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
