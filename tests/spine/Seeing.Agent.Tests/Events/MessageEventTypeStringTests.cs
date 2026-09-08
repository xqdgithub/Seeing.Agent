using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class MessageEventTypeStringTests
{
    [Theory]
    [InlineData(MessageEventType.LoopStart, "loop.start")]
    [InlineData(MessageEventType.LoopComplete, "loop.complete")]
    [InlineData(MessageEventType.StreamStart, "stream.start")]
    [InlineData(MessageEventType.StreamDelta, "stream.delta")]
    [InlineData(MessageEventType.StreamComplete, "stream.complete")]
    [InlineData(MessageEventType.ToolCallPending, "tool.call.pending")]
    [InlineData(MessageEventType.ToolCallRunning, "tool.call.running")]
    [InlineData(MessageEventType.ToolCallComplete, "tool.call.complete")]
    [InlineData(MessageEventType.PermissionRequest, "permission.request")]
    [InlineData(MessageEventType.PermissionResponse, "permission.response")]
    [InlineData(MessageEventType.LoopCancelled, "loop.cancelled")]
    [InlineData(MessageEventType.Error, "error")]
    [InlineData(MessageEventType.CommandResult, "command.result")]
    [InlineData(MessageEventType.BudgetStatus, "budget.status")]
    [InlineData(MessageEventType.BudgetWarning, "budget.warning")]
    [InlineData(MessageEventType.Navigate, "navigate")]
    [InlineData(MessageEventType.TodoUpdate, "todo.update")]
    [InlineData(MessageEventType.ModeUpdate, "mode.update")]
    public void Constants_MapToDotNotationStrings(string value, string expected)
    {
        value.Should().Be(expected);
    }
}