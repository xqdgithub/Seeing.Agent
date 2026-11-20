using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permissions;

/// <summary>
/// 门闸只保留开关与文案，判定委托 <see cref="ISessionRootSet"/>。
/// 旧语义「会话 cwd 自动作为合法根」已移除。
/// </summary>
public class WorkspacePathGateSessionRootTests
{
    private static IOptionsMonitor<SeeingAgentOptions> Monitor(bool restrict)
    {
        var m = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        m.Setup(o => o.CurrentValue).Returns(new SeeingAgentOptions
        {
            Workspace = new WorkspaceOptions { RestrictToWorkspace = restrict }
        });
        return m.Object;
    }

    [Fact]
    public void EnsureAllowed_ShouldDelegateToIsAuthorized()
    {
        var rootSet = new Mock<ISessionRootSet>();
        rootSet.Setup(r => r.IsAuthorized("s1", It.IsAny<string>())).Returns(true);
        var gate = new WorkspacePathGate(rootSet.Object, Monitor(restrict: true));

        gate.EnsureAllowed("s1", "/x/a.txt").Should().BeNull();
        rootSet.Verify(r => r.IsAuthorized("s1", "/x/a.txt"), Times.Once);
    }

    [Fact]
    public void EnsureAllowed_WhenRestrictOff_ShouldPassWithoutConsultingRootSet()
    {
        var rootSet = new Mock<ISessionRootSet>();
        var gate = new WorkspacePathGate(rootSet.Object, Monitor(restrict: false));

        gate.EnsureAllowed("s1", "/x/a.txt").Should().BeNull();
        rootSet.Verify(r => r.IsAuthorized(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void EnsureWithinWorkspace_ShouldUseIsTrusted_NotIsAuthorized()
    {
        var rootSet = new Mock<ISessionRootSet>();
        rootSet.Setup(r => r.IsTrusted("s1", It.IsAny<string>())).Returns(false);
        var gate = new WorkspacePathGate(rootSet.Object, Monitor(restrict: false));

        gate.EnsureWithinWorkspace("s1", "/x/a.txt").Should().NotBeNull();
        rootSet.Verify(r => r.IsAuthorized(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void EnsureWithinWorkspace_WhitelistedPath_ShouldStillReject()
    {
        // IsTrusted 不含临时白名单：附件准入不受 add_workspace_path 影响。
        var rootSet = new Mock<ISessionRootSet>();
        rootSet.Setup(r => r.IsAuthorized("s1", It.IsAny<string>())).Returns(true);
        rootSet.Setup(r => r.IsTrusted("s1", It.IsAny<string>())).Returns(false);
        var gate = new WorkspacePathGate(rootSet.Object, Monitor(restrict: true));

        gate.EnsureAllowed("s1", "/outside/a.txt").Should().BeNull();
        gate.EnsureWithinWorkspace("s1", "/outside/a.txt").Should().NotBeNull();
    }

    [Fact]
    public void ResolveAllowed_RelativePath_ShouldUseBase()
    {
        var rootSet = new Mock<ISessionRootSet>();
        rootSet.Setup(r => r.IsAuthorized("s1", It.IsAny<string>())).Returns(true);
        var gate = new WorkspacePathGate(rootSet.Object, Monitor(restrict: true));
        var baseDir = Path.Combine(Path.GetTempPath(), "gate-" + Guid.NewGuid().ToString("N"));

        var error = gate.ResolveAllowed("s1", "a.txt", baseDir, out var full);

        error.Should().BeNull();
        full.Should().Be(Path.GetFullPath(Path.Combine(baseDir, "a.txt")));
    }

    [Fact]
    public void ResolveAllowed_MissingBaseForRelativePath_ShouldFail()
    {
        var gate = new WorkspacePathGate(new Mock<ISessionRootSet>().Object, Monitor(restrict: true));

        var error = gate.ResolveAllowed("s1", "a.txt", baseDirectory: null, out _);

        error.Should().NotBeNull();
    }
}
