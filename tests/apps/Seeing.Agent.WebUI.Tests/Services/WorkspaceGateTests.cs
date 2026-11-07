using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class WorkspaceGateTests
{
    [Theory]
    [InlineData("")]
    [InlineData("sessions")]
    [InlineData("settings")]
    [InlineData("session/abc")]
    public void Unresolved_ShouldForceHome_ForEveryPathButHome(string path)
    {
        WorkspaceGate.ShouldForceWorkspaceHome(isResolved: false, relativePath: path)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("workspaces")]
    [InlineData("workspaces/")]
    [InlineData("workspaces?x=1")]
    public void Unresolved_ShouldNotRedirectFromHome(string path) =>
        WorkspaceGate.ShouldForceWorkspaceHome(false, path).Should().BeFalse();

    [Fact]
    public void Resolved_ShouldNeverForceHome() =>
        WorkspaceGate.ShouldForceWorkspaceHome(true, "").Should().BeFalse();
}
