using FluentAssertions;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Llm;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.Tests.Mapping;

public class GatewayEventMapperTests
{
    private const string SessionId = "ses_test";
    private const string LoopId = "loop_001";
    private static readonly DateTime TestTimestamp = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Map_LoopStart_ShouldMapToResponseInProgress()
    {
        var source = new LoopStartEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            UserInput = "hello",
            Timestamp = TestTimestamp
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Response);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.SessionId.Should().Be(SessionId);
        result.LoopId.Should().Be(LoopId);
        result.Data!.UserInput.Should().Be("hello");
        result.Timestamp.Should().Be(TestTimestamp);
        result.SourceType.Should().Be(nameof(MessageEventType.LoopStart));
    }

    [Fact]
    public void Map_StreamStart_ShouldMapToContentInProgressWithStep()
    {
        var source = new StreamStartEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            Step = 2,
            Timestamp = TestTimestamp
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Content);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.Data!.Step.Should().Be(2);
        result.SourceType.Should().Be(nameof(MessageEventType.StreamStart));
    }

    [Fact]
    public void Map_StreamDelta_ShouldMapToContentInProgressWithDelta()
    {
        var source = new StreamDeltaEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            ContentDelta = "partial",
            ReasoningDelta = "thinking"
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Content);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.Data!.Delta.Should().BeTrue();
        result.Data.Text.Should().Be("partial");
        result.Data.Reasoning.Should().BeNull();
        result.Data.StreamKind.Should().Be("content");
    }

    [Fact]
    public void Map_StreamDelta_ReasoningOnly_WithFilterThinking_ShouldReturnFalse()
    {
        var source = new StreamDeltaEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            ReasoningDelta = "thinking"
        };

        var ok = GatewayEventMapper.TryMap(source, out var gatewayEvent);

        ok.Should().BeFalse();
        gatewayEvent.Should().BeNull();
    }

    [Fact]
    public void Map_StreamDelta_ReasoningOnly_WithoutFilterThinking_ShouldMapReasoning()
    {
        var source = new StreamDeltaEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            ReasoningDelta = "thinking"
        };

        var result = GatewayEventMapper.Map(source, new GatewayEventMapperOptions { FilterThinking = false });

        result.Data!.StreamKind.Should().Be("reasoning");
        result.Data.Reasoning.Should().Be("thinking");
    }

    [Fact]
    public void Map_StreamComplete_Assistant_ShouldMapToMessageCompleted()
    {
        var source = new StreamCompleteEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            Message = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "done",
                ReasoningContent = "reasoned"
            },
            Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Message);
        result.Status.Should().Be(GatewayEventStatus.Completed);
        result.Data!.Role.Should().Be(ChatRole.Assistant);
        result.Data.MessageRole.Should().Be("assistant");
        result.Data.Text.Should().Be("done");
        result.Data.Usage!.TotalTokens.Should().Be(15);
    }

    [Fact]
    public void Map_StreamComplete_Tool_ShouldMapToContentInProgress()
    {
        var source = new StreamCompleteEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            Message = new ChatMessage
            {
                Role = ChatRole.Tool,
                Content = "tool result"
            }
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Content);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.Data!.MessageRole.Should().Be("tool");
        result.Data.StreamKind.Should().Be("tool");
    }

    [Theory]
    [InlineData(MessageEventType.ToolCallPending, ToolCallStatus.Pending)]
    [InlineData(MessageEventType.ToolCallRunning, ToolCallStatus.Running)]
    [InlineData(MessageEventType.ToolCallComplete, ToolCallStatus.Success)]
    public void Map_ToolCallEvents_ShouldMapToContentInProgress(
        MessageEventType eventType,
        ToolCallStatus status)
    {
        var source = new ToolCallEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            Type = eventType,
            ToolCallId = "call_1",
            ToolName = "read_file",
            Arguments = new { path = "/tmp/a.txt" },
            Status = status,
            Output = status == ToolCallStatus.Success ? "ok" : null,
            Title = "Read file",
            Duration = TimeSpan.FromMilliseconds(120)
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Content);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.Data!.ToolCallId.Should().Be("call_1");
        result.Data.ToolName.Should().Be("read_file");
        result.Data.ToolStatus.Should().Be(status.ToString().ToLowerInvariant());
        result.Data.ToolOutput.Should().Be(status == ToolCallStatus.Success ? "ok" : null);
        result.Data.ToolTitle.Should().Be("Read file");
        result.Data.Duration.Should().Be(TimeSpan.FromMilliseconds(120));
    }

    [Fact]
    public void Map_PermissionRequest_ShouldMapToPermissionInProgress()
    {
        var source = new PermissionRequestEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            PermissionId = "perm_1",
            PermissionKind = "tool",
            Resource = "bash",
            Message = "Allow shell?",
            RiskLevel = "high"
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Permission);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.Data!.PermissionId.Should().Be("perm_1");
        result.Data.PermissionKind.Should().Be("tool");
        result.Data.Resource.Should().Be("bash");
        result.Data.PermissionMessage.Should().Be("Allow shell?");
        result.Data.RiskLevel.Should().Be("high");
    }

    [Fact]
    public void Map_PermissionResponse_ShouldMapToPermissionCompleted()
    {
        var source = new PermissionResponseEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            PermissionId = "perm_1",
            Decision = "allow",
            Reason = "ok"
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Permission);
        result.Status.Should().Be(GatewayEventStatus.Completed);
        result.Data!.PermissionDecision.Should().Be("allow");
        result.Data.PermissionReason.Should().Be("ok");
    }

    [Fact]
    public void Map_LoopComplete_ShouldMapToResponseCompletedWithUsage()
    {
        var source = new LoopCompleteEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            TotalSteps = 3,
            Success = true,
            Duration = TimeSpan.FromSeconds(2),
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Response);
        result.Status.Should().Be(GatewayEventStatus.Completed);
        result.Data!.TotalSteps.Should().Be(3);
        result.Data.Success.Should().BeTrue();
        result.Data.Duration.Should().Be(TimeSpan.FromSeconds(2));
        result.Data.Usage!.TotalTokens.Should().Be(150);
    }

    [Fact]
    public void Map_LoopCancelled_ShouldMapToResponseCancelled()
    {
        var source = new LoopCancelledEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            Reason = "user",
            CompletedSteps = 1
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Response);
        result.Status.Should().Be(GatewayEventStatus.Cancelled);
        result.Data!.CancelReason.Should().Be("user");
        result.Data.CompletedSteps.Should().Be(1);
    }

    [Fact]
    public void Map_Error_ShouldMapToErrorFailed()
    {
        var source = new ErrorEvent
        {
            SessionId = SessionId,
            LoopId = LoopId,
            Message = "boom",
            Source = "agent"
        };

        var result = GatewayEventMapper.Map(source);

        result.Object.Should().Be(GatewayEventObject.Error);
        result.Status.Should().Be(GatewayEventStatus.Failed);
        result.Data!.Error.Should().Be("boom");
        result.Data.ErrorSource.Should().Be("agent");
    }

    [Fact]
    public void MapPendingPermission_ShouldMatchPermissionRequestPayload()
    {
        var pending = new GatewayPendingPermission
        {
            PermissionId = "perm_1",
            SessionId = SessionId,
            LoopId = LoopId,
            PermissionKind = "tool",
            Resource = "bash",
            Message = "Allow?",
            RiskLevel = "high",
            CreatedAt = TestTimestamp
        };

        var result = GatewayEventMapper.MapPendingPermission(pending);

        result.Object.Should().Be(GatewayEventObject.Permission);
        result.Status.Should().Be(GatewayEventStatus.InProgress);
        result.Data!.PermissionId.Should().Be("perm_1");
        result.SourceType.Should().Be(nameof(MessageEventType.PermissionRequest));
    }
}
