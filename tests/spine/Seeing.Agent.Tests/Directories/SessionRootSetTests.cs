using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Directories;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.Directories;

/// <summary>目录判定唯一权威：白名单只影响 IsAuthorized，绝不进入 IsTrusted。</summary>
public class SessionRootSetTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "root_set_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (SessionRootSet Roots, PermissionGrantStore Grants) Create(
        string projectRoot, params string[] collaborations)
    {
        var session = SessionData.Create();
        session.CollaborationDirectories = collaborations
            .Select(p => new CollaborationDirectory(p))
            .ToList();

        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get(It.IsAny<string>())).Returns(session);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.ProjectSeeingDirectory).Returns(Path.Combine(projectRoot, ".seeing"));

        var grants = new PermissionGrantStore();
        var roots = new SessionRootSet(
            sessions.Object, workspace.Object, grants,
            NullLogger<SessionRootSet>.Instance);

        return (roots, grants);
    }

    [Fact]
    public void IsAuthorized_And_IsTrusted_ShouldBothAllowCollaborationDirectory()
    {
        var project = NewDir();
        var collab = NewDir();
        var (roots, _) = Create(project, collab);

        roots.IsAuthorized("s1", Path.Combine(collab, "a.txt")).Should().BeTrue();
        roots.IsTrusted("s1", Path.Combine(collab, "a.txt")).Should().BeTrue();
    }

    [Fact]
    public void IsTrusted_ShouldNotIncludeTemporaryGrant()
    {
        var project = NewDir();
        var outside = NewDir();
        var (roots, grants) = Create(project);
        grants.AddSessionDirectory("s1", outside);

        var file = Path.Combine(outside, "a.txt");
        roots.IsAuthorized("s1", file).Should().BeTrue();
        roots.IsTrusted("s1", file).Should().BeFalse("附件准入不得享受临时白名单豁免");
    }

    [Fact]
    public void UnregisteredOutsidePath_ShouldBeDeniedByBoth()
    {
        var project = NewDir();
        var outside = NewDir();
        var (roots, _) = Create(project);

        var file = Path.Combine(outside, "a.txt");
        roots.IsAuthorized("s1", file).Should().BeFalse();
        roots.IsTrusted("s1", file).Should().BeFalse();
    }

    [Fact]
    public void Roots_ShouldContainProjectAndCollaborations()
    {
        var project = NewDir();
        var collab = NewDir();
        var (roots, _) = Create(project, collab);

        var all = roots.Roots("s1");

        all.Should().Contain(r => r.Kind == SessionRootKind.Project);
        all.Should().Contain(r => r.Kind == SessionRootKind.Collaboration);
    }
}
