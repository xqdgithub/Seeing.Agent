using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permissions;

public class WorkspacePathGateSessionRootTests
{
    private static IOptionsMonitor<SeeingAgentOptions> RestrictOn()
    {
        var m = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        m.Setup(o => o.CurrentValue).Returns(new SeeingAgentOptions
        {
            Workspace = new WorkspaceOptions { RestrictToWorkspace = true }
        });
        return m.Object;
    }

    private static IWorkspaceProvider WorkspaceAt(string root)
    {
        var w = new Mock<IWorkspaceProvider>();
        w.SetupGet(x => x.ProjectSeeingDirectory).Returns(Path.Combine(root, ".seeing"));
        return w.Object;
    }

    [Fact]
    public void SessionRoot_InsideSessionCwd_ShouldAllow()
    {
        var global = Path.Combine(Path.GetTempPath(), "ws-global-" + Guid.NewGuid().ToString("N"));
        var sessionCwd = Path.Combine(Path.GetTempPath(), "ws-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(global);
        Directory.CreateDirectory(sessionCwd);
        try
        {
            var resolver = new Mock<ISessionWorkspaceResolver>();
            resolver.Setup(r => r.ResolveWorkingDirectory("s1")).Returns(sessionCwd);
            var gate = new WorkspacePathGate(WorkspaceAt(global), new Mock<IPermissionGrantStore>().Object, RestrictOn(), resolver.Object);
            gate.EnsureAllowed("s1", Path.Combine(sessionCwd, "a.txt")).Should().BeNull();
        }
        finally { Directory.Delete(global, true); Directory.Delete(sessionCwd, true); }
    }

    [Fact]
    public void OutsideBothRoots_NotWhitelisted_ShouldReject()
    {
        var global = Path.Combine(Path.GetTempPath(), "ws-global-" + Guid.NewGuid().ToString("N"));
        var sessionCwd = Path.Combine(Path.GetTempPath(), "ws-session-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "ws-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(global); Directory.CreateDirectory(sessionCwd); Directory.CreateDirectory(other);
        try
        {
            var resolver = new Mock<ISessionWorkspaceResolver>();
            resolver.Setup(r => r.ResolveWorkingDirectory("s1")).Returns(sessionCwd);
            var gate = new WorkspacePathGate(WorkspaceAt(global), new Mock<IPermissionGrantStore>().Object, RestrictOn(), resolver.Object);
            gate.EnsureAllowed("s1", Path.Combine(other, "a.txt")).Should().NotBeNull();
        }
        finally { Directory.Delete(global, true); Directory.Delete(sessionCwd, true); Directory.Delete(other, true); }
    }
}
