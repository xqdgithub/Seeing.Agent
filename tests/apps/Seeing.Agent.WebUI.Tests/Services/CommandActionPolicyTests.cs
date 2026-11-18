using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class CommandActionPolicyTests
{
    [Fact]
    public async Task ModelSet_Acp_Should_Be_Unavailable()
    {
        var result = await CommandActionPolicy.ModelSetAsync("m1", isAcpPassthrough: true, _ => Task.CompletedTask);
        result.Status.Should().Be(HostActionStatus.Unavailable);
        result.Message.Should().Contain("ACP");
    }

    [Fact]
    public async Task ModelSet_Empty_Should_Be_NotHandled()
    {
        var result = await CommandActionPolicy.ModelSetAsync(" ", isAcpPassthrough: false, _ => Task.CompletedTask);
        result.Status.Should().Be(HostActionStatus.NotHandled);
    }

    [Fact]
    public async Task ModelSet_Normal_Should_Invoke_Delegate_And_Handle()
    {
        string? applied = null;
        var result = await CommandActionPolicy.ModelSetAsync(
            "m1",
            isAcpPassthrough: false,
            id => { applied = id; return Task.CompletedTask; });

        applied.Should().Be("m1");
        result.Status.Should().Be(HostActionStatus.Handled);
    }

    [Fact]
    public async Task AgentSet_Acp_Should_Still_Invoke_So_User_Can_Leave_Acp()
    {
        string? applied = null;
        var result = await CommandActionPolicy.AgentSetAsync(
            "build",
            isAcpPassthrough: true,
            id => { applied = id; return Task.CompletedTask; });

        applied.Should().Be("build", "ACP 会话仍必须能切回 Native Agent，否则用户被困在 ACP");
        result.Status.Should().Be(HostActionStatus.Handled);
    }

    [Fact]
    public async Task AgentSet_Normal_Should_Invoke_Delegate_And_Handle()
    {
        string? applied = null;
        var result = await CommandActionPolicy.AgentSetAsync(
            "build",
            isAcpPassthrough: false,
            id => { applied = id; return Task.CompletedTask; });

        applied.Should().Be("build");
        result.Status.Should().Be(HostActionStatus.Handled);
    }
}
