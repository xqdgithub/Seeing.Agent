using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class OneShotOutputTests
{
    private static (OneShotOutput output, StringWriter stdout, StringWriter stderr) Create(bool stream)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        return (new OneShotOutput(stdout, stderr, stream), stdout, stderr);
    }

    [Fact]
    public void Apply_DefaultMode_ShouldNotWriteToStdoutUntilFinal()
    {
        var (output, stdout, _) = Create(stream: false);

        output.Apply(new StreamCompleteEvent
        {
            SessionId = "ses_1",
            Message = new ChatMessage { Role = ChatRole.Assistant, Content = "最终答案" }
        });

        stdout.ToString().Should().BeEmpty();

        output.WriteFinal();
        stdout.ToString().Should().Be("最终答案" + Environment.NewLine);
    }

    [Fact]
    public void Apply_MultipleStreamComplete_ShouldKeepLastContent()
    {
        var (output, stdout, _) = Create(stream: false);

        output.Apply(new StreamCompleteEvent
        {
            SessionId = "ses_1",
            Message = new ChatMessage { Content = "第一步" }
        });
        output.Apply(new StreamCompleteEvent
        {
            SessionId = "ses_1",
            Message = new ChatMessage { Content = "第二步最终" }
        });

        output.WriteFinal();
        stdout.ToString().Should().Be("第二步最终" + Environment.NewLine);
    }

    [Fact]
    public void Apply_StreamMode_ShouldWriteSingleLineJsonWithType()
    {
        var (output, stdout, _) = Create(stream: true);

        output.Apply(new StreamDeltaEvent
        {
            SessionId = "ses_1",
            LoopId = "loop_1",
            ContentDelta = "你好"
        });

        var line = stdout.ToString().Trim();
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("type").GetString().Should().Be("stream.delta");
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("ses_1");
        doc.RootElement.GetProperty("contentDelta").GetString().Should().Be("你好");
    }

    [Fact]
    public void Apply_ExecutionComplete_ShouldSerializeStatus()
    {
        var (output, stdout, _) = Create(stream: true);

        output.Apply(new ExecutionCompleteEvent
        {
            SessionId = "ses_1",
            ExecutionId = "exec_1",
            Status = ExecutionStatus.Completed
        });

        using var doc = JsonDocument.Parse(stdout.ToString().Trim());
        doc.RootElement.GetProperty("type").GetString().Should().Be("loop.complete");
        doc.RootElement.GetProperty("status").GetString().Should().Be("Completed");
    }

    [Fact]
    public void Apply_StreamMode_WriteFinal_ShouldNotDuplicateContent()
    {
        var (output, stdout, _) = Create(stream: true);

        output.Apply(new StreamCompleteEvent
        {
            SessionId = "ses_1",
            Message = new ChatMessage { Content = "答案" }
        });
        var afterApply = stdout.ToString();
        output.WriteFinal();

        stdout.ToString().Should().Be(afterApply);
    }

    [Fact]
    public void Apply_ErrorEvent_ShouldSetFlagAndWriteDiagnosticToStderr()
    {
        var (output, stdout, stderr) = Create(stream: false);

        output.Apply(new ErrorEvent { SessionId = "ses_1", Message = "boom" });

        output.HasError.Should().BeTrue();
        stderr.ToString().Should().Contain("boom");
        stdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Apply_ToolEvent_DefaultMode_ShouldNotWriteAnywhere()
    {
        var (output, stdout, stderr) = Create(stream: false);

        output.Apply(new ToolCallEvent
        {
            SessionId = "ses_1",
            ToolCallId = "call_1",
            ToolName = "websearch",
            Status = ToolCallStatus.Running
        });

        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Apply_ErrorEvent_StreamMode_ShouldNotWriteStderr()
    {
        var (output, stdout, stderr) = Create(stream: true);

        output.Apply(new ErrorEvent { SessionId = "ses_1", Message = "boom" });

        output.HasError.Should().BeTrue();
        stderr.ToString().Should().BeEmpty();
        stdout.ToString().Should().Contain("\"type\":\"error\"");
    }
}
