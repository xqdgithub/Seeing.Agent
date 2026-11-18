using FluentAssertions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class HostActionRegistryTests
{
    [Fact]
    public async Task Invoke_Unknown_Action_Should_Return_NotHandled()
    {
        var registry = new HostActionRegistry();
        var result = await registry.InvokeAsync("s1", new CommandAction { Name = "nope" });
        result.Status.Should().Be(HostActionStatus.NotHandled);
    }

    [Fact]
    public async Task Invoke_Should_Isolate_By_Session()
    {
        var registry = new HostActionRegistry();
        using var registration = registry.Register("s1", "x", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled)));

        (await registry.InvokeAsync("s1", new CommandAction { Name = "x" })).Status
            .Should().Be(HostActionStatus.Handled);
        (await registry.InvokeAsync("s2", new CommandAction { Name = "x" })).Status
            .Should().Be(HostActionStatus.NotHandled);
    }

    [Fact]
    public async Task ReRegister_Same_Key_Should_Let_Last_Win_And_Old_Dispose_Should_Not_Remove_New()
    {
        var registry = new HostActionRegistry();
        var first = registry.Register("s1", "x", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled, "first")));
        using var second = registry.Register("s1", "x", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled, "second")));
        first.Dispose();

        var result = await registry.InvokeAsync("s1", new CommandAction { Name = "x" });
        result.Message.Should().Be("second");
    }

    [Fact]
    public async Task Rekey_Should_Move_Registration_To_New_Session()
    {
        var registry = new HostActionRegistry();
        var first = registry.Register("s1", "x", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled, "old")));

        // 模拟窗口切到 s2：撤销旧键，按新会话注册
        first.Dispose();
        using var second = registry.Register("s2", "x", _ =>
            Task.FromResult(new HostActionResult(HostActionStatus.Handled, "new")));

        (await registry.InvokeAsync("s1", new CommandAction { Name = "x" })).Status
            .Should().Be(HostActionStatus.NotHandled);
        (await registry.InvokeAsync("s2", new CommandAction { Name = "x" })).Message
            .Should().Be("new");
    }

    [Fact]
    public async Task Handler_Throw_Should_Return_NotHandled()
    {
        var registry = new HostActionRegistry();
        using var registration = registry.Register("s1", "boom", _ =>
            throw new InvalidOperationException("x"));

        var result = await registry.InvokeAsync("s1", new CommandAction { Name = "boom" });
        result.Status.Should().Be(HostActionStatus.NotHandled);
    }
}
