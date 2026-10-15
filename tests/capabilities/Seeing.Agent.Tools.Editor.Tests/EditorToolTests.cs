using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Editor;
using Xunit;

namespace Seeing.Agent.Tools.Editor.Tests;

public class EditorToolTests
{
    private const string DataBegin = "<<<EDITOR_DATA_BEGIN>>>";
    private const string DataEnd = "<<<EDITOR_DATA_END>>>";

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static EditorCommandTool CommandTool() => new(NullLogger<EditorCommandTool>.Instance);

    private static EditorQueryTool QueryTool() => new(NullLogger<EditorQueryTool>.Instance);

    private static EditorListCommandsTool ListTool() => new(NullLogger<EditorListCommandsTool>.Instance);

    private static ToolContext Context(
        IHostRequestManager? manager = null,
        IHostSurfaceRegistry? surfaces = null)
    {
        var context = new ToolContext
        {
            SessionId = "s1",
            MessageId = "m1",
            CallId = "c1"
        };

        if (manager is not null || surfaces is not null)
        {
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            if (surfaces is not null)
                services.AddSingleton(surfaces);
            context.Services = services.BuildServiceProvider();
        }

        return context;
    }

    private static Mock<IHostRequestManager> ManagerReturning(HostResponse response)
    {
        var manager = new Mock<IHostRequestManager>();
        manager.Setup(m => m.BeginAsync(It.IsAny<HostRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestTicket("r1", "s1"));
        manager.Setup(m => m.WaitAsync(It.IsAny<RequestTicket>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return manager;
    }

    private static Mock<IHostSurfaceRegistry> Surfaces(bool canSurface)
    {
        var registry = new Mock<IHostSurfaceRegistry>();
        registry.Setup(r => r.CanSurface(It.IsAny<string>())).Returns(canSurface);
        return registry;
    }

    private static HostResponse Completed(object? payload = null) => new()
    {
        RequestId = "r1",
        SessionId = "s1",
        Status = HostResponseStatus.Completed,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload)
    };

    private static HostResponse Status(HostResponseStatus status, string? reason = null) => new()
    {
        RequestId = "r1",
        SessionId = "s1",
        Status = status,
        Reason = reason
    };

    [Fact]
    public async Task Command_MissingCommandId_Should_Fail()
    {
        var result = await CommandTool().ExecuteAsync(
            Args(new { }), Context(ManagerReturning(Completed()).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("commandId");
    }

    [Fact]
    public async Task Command_MissingManager_Should_Fail()
    {
        var result = await CommandTool().ExecuteAsync(
            Args(new { commandId = "editor.foldAll" }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("宿主请求通道未接入");
    }

    [Fact]
    public async Task Command_CannotSurface_Should_Fail_And_NotBegin()
    {
        var manager = ManagerReturning(Completed());
        var surfaces = Surfaces(false);

        var result = await CommandTool().ExecuteAsync(
            Args(new { commandId = "editor.foldAll" }),
            Context(manager.Object, surfaces.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无可用编辑器连接");
        manager.Verify(
            m => m.BeginAsync(It.IsAny<HostRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Command_Completed_Should_Succeed()
    {
        var manager = ManagerReturning(Completed(new { applied = true }));

        var result = await CommandTool().ExecuteAsync(
            Args(new { commandId = "editor.action.formatDocument" }),
            Context(manager.Object, Surfaces(true).Object));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("命令已执行");
        result.Metadata["status"].Should().Be("completed");
    }

    [Fact]
    public async Task Command_Unavailable_Should_Fail()
    {
        var manager = ManagerReturning(Status(HostResponseStatus.Unavailable));

        var result = await CommandTool().ExecuteAsync(
            Args(new { commandId = "editor.foldAll" }), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无可用编辑器连接");
    }

    [Fact]
    public async Task Command_Timeout_Should_Fail()
    {
        var manager = ManagerReturning(Status(HostResponseStatus.Timeout));

        var result = await CommandTool().ExecuteAsync(
            Args(new { commandId = "editor.foldAll" }), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("限定时间内");
    }

    [Fact]
    public async Task Command_Cancelled_Should_Fail()
    {
        var manager = ManagerReturning(Status(HostResponseStatus.Cancelled));

        var result = await CommandTool().ExecuteAsync(
            Args(new { commandId = "editor.foldAll" }), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("已取消");
    }

    [Fact]
    public async Task Query_Completed_Should_WrapPayload()
    {
        var manager = ManagerReturning(Completed(new { kind = "cursor", line = 3 }));

        var result = await QueryTool().ExecuteAsync(
            Args(new { kind = "cursor" }), Context(manager.Object, Surfaces(true).Object));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain(DataBegin).And.Contain(DataEnd);
    }

    [Fact]
    public async Task Query_InvalidKind_Should_Fail()
    {
        var result = await QueryTool().ExecuteAsync(
            Args(new { kind = "bogus" }), Context(ManagerReturning(Completed()).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("kind");
    }

    [Fact]
    public async Task Query_Unavailable_Should_Fail()
    {
        var manager = ManagerReturning(Status(HostResponseStatus.Unavailable));

        var result = await QueryTool().ExecuteAsync(
            Args(new { kind = "selection" }), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无可用编辑器连接");
    }

    [Fact]
    public async Task ListCommands_Completed_Should_Succeed()
    {
        var manager = ManagerReturning(Completed(new[]
        {
            new { id = "editor.foldAll", label = "Fold All" }
        }));

        var result = await ListTool().ExecuteAsync(
            Args(new { }), Context(manager.Object, Surfaces(true).Object));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain(DataBegin).And.Contain("editor.foldAll");
    }

    [Fact]
    public async Task ListCommands_Unavailable_Should_Fail()
    {
        var manager = ManagerReturning(Status(HostResponseStatus.Unavailable));

        var result = await ListTool().ExecuteAsync(
            Args(new { }), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无可用编辑器连接");
    }
}
