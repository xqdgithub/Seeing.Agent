using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class AgentFileChangeBridgeTests
{
    private static AgentFileChangeBridge CreateBridge()
    {
        var factory = new Mock<IFileWorkspaceFactory>();
        factory.Setup(f => f.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sessionId, CancellationToken _) =>
            {
                var ws = new Mock<IFileWorkspace>();
                ws.SetupGet(w => w.SessionId).Returns(sessionId);
                ws.Setup(w => w.GetResourceId(It.IsAny<string>()))
                    .Returns((string p) => new FileResourceId("root", p));
                return ws.Object;
            });
        return new AgentFileChangeBridge(factory.Object);
    }

    private static ToolCallEvent ToolCall(string name, ToolCallStatus status, object? path = null) =>
        new()
        {
            SessionId = "s1",
            ToolCallId = "c1",
            ToolName = name,
            Status = status,
            Metadata = path is null
                ? null
                : new Dictionary<string, object> { ["path"] = path }
        };

    [Fact]
    public void OpenFile_ShouldRaiseOpenRequested_NotAgentChanged()
    {
        var bridge = CreateBridge();
        FileResourceId? opened = null;
        var changedCount = 0;
        bridge.OpenRequested += (id, _, _) => { opened = id; return Task.CompletedTask; };
        bridge.AgentChanged += (_, _) => { changedCount++; return Task.CompletedTask; };

        bridge.OnEvent(ToolCall("open_file", ToolCallStatus.Success, @"E:\ws\a.txt"));

        SpinWait.SpinUntil(() => opened is not null, 2000).Should().BeTrue();
        opened!.Value.RelativePath.Should().Be(@"E:\ws\a.txt");
        changedCount.Should().Be(0);
    }

    [Fact]
    public void OpenFile_WithPosition_ShouldCarryLineColumn()
    {
        var bridge = CreateBridge();
        int? line = null, column = null;
        bridge.OpenRequested += (_, l, c) => { line = l; column = c; return Task.CompletedTask; };

        var evt = ToolCall("open_file", ToolCallStatus.Success, @"E:\ws\a.txt") with
        {
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { path = @"E:\ws\a.txt", line = 12, column = 4 })
        };
        bridge.OnEvent(evt);

        SpinWait.SpinUntil(() => line is not null, 2000).Should().BeTrue();
        line.Should().Be(12);
        column.Should().Be(4);
    }

    [Fact]
    public void Write_ShouldRaiseAgentChanged_NotOpenRequested()
    {
        var bridge = CreateBridge();
        var openedCount = 0;
        string? changedPath = null;
        bridge.OpenRequested += (_, _, _) => { openedCount++; return Task.CompletedTask; };
        bridge.AgentChanged += (id, _) => { changedPath = id.RelativePath; return Task.CompletedTask; };

        bridge.OnEvent(ToolCall("write", ToolCallStatus.Success, @"E:\ws\b.txt"));

        SpinWait.SpinUntil(() => changedPath is not null, 2000).Should().BeTrue();
        changedPath.Should().Be(@"E:\ws\b.txt");
        openedCount.Should().Be(0);
    }

    [Fact]
    public void OpenFile_NonSuccess_ShouldNotRaise()
    {
        var bridge = CreateBridge();
        var raised = false;
        bridge.OpenRequested += (_, _, _) => { raised = true; return Task.CompletedTask; };

        bridge.OnEvent(ToolCall("open_file", ToolCallStatus.Failed, @"E:\ws\a.txt"));

        raised.Should().BeFalse();
    }
}
