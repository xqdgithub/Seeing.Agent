using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionSummaryViewTests
{
    // ---- PreviewSnippet ----

    [Fact]
    public void PreviewSnippet_ShortText_ShouldReturnAsIs()
    {
        SessionSummaryView.PreviewSnippet("你好，世界")
            .Should().Be("你好，世界");
    }

    [Fact]
    public void PreviewSnippet_LongText_ShouldTruncateAt120WithEllipsis()
    {
        var text = new string('a', 200);

        var result = SessionSummaryView.PreviewSnippet(text);

        result.Should().Be(new string('a', 120) + "…");
        result.Length.Should().Be(121);
    }

    [Fact]
    public void PreviewSnippet_Empty_ShouldReturnNoMessage()
    {
        SessionSummaryView.PreviewSnippet(null).Should().Be("暂无消息");
        SessionSummaryView.PreviewSnippet("   ").Should().Be("暂无消息");
    }

    // ---- CompactSnippet ----

    [Fact]
    public void CompactSnippet_ShortText_ShouldReturnAsIs()
    {
        SessionSummaryView.CompactSnippet("短摘要")
            .Should().Be("短摘要");
    }

    [Fact]
    public void CompactSnippet_LongText_ShouldTruncateAround20WithEllipsis()
    {
        var text = new string('b', 30);

        var result = SessionSummaryView.CompactSnippet(text);

        result.Should().Be(new string('b', 20) + "…");
        result.Length.Should().Be(21);
    }

    [Fact]
    public void CompactSnippet_Empty_ShouldReturnNoMessage()
    {
        SessionSummaryView.CompactSnippet(null).Should().Be("暂无消息");
        SessionSummaryView.CompactSnippet("").Should().Be("暂无消息");
    }

    // ---- Classify ----

    [Fact]
    public void Classify_Executing_ShouldReturnRunning()
    {
        SessionSummaryView.Classify(isExecuting: true, status: null)
            .Should().Be(SummaryState.Running);
    }

    [Fact]
    public void Classify_Queued_ShouldReturnQueued()
    {
        SessionSummaryView.Classify(isExecuting: false, status: ExecutionStatus.Queued)
            .Should().Be(SummaryState.Queued);
    }

    [Fact]
    public void Classify_Failed_ShouldReturnFailed()
    {
        SessionSummaryView.Classify(isExecuting: false, status: ExecutionStatus.Failed)
            .Should().Be(SummaryState.Failed);
    }

    [Fact]
    public void Classify_Cancelled_ShouldReturnCancelled()
    {
        SessionSummaryView.Classify(isExecuting: false, status: ExecutionStatus.Cancelled)
            .Should().Be(SummaryState.Cancelled);
    }

    [Theory]
    [InlineData(ExecutionStatus.Completed)]
    [InlineData(null)]
    public void Classify_Otherwise_ShouldReturnDone(ExecutionStatus? status)
    {
        SessionSummaryView.Classify(isExecuting: false, status: status)
            .Should().Be(SummaryState.Done);
    }

    // ---- Elapsed ----

    [Fact]
    public void Elapsed_Null_ShouldReturnEmpty()
    {
        SessionSummaryView.Elapsed(null, DateTime.Now).Should().BeEmpty();
    }

    [Fact]
    public void Elapsed_Seconds_ShouldReturnMmSs()
    {
        var start = new DateTime(2026, 9, 18, 10, 0, 0);

        SessionSummaryView.Elapsed(start, start.AddSeconds(5)).Should().Be("00:05");
    }

    [Fact]
    public void Elapsed_Minutes_ShouldReturnMmSs()
    {
        var start = new DateTime(2026, 9, 18, 10, 0, 0);

        SessionSummaryView.Elapsed(start, start.AddSeconds(65)).Should().Be("01:05");
    }

    [Fact]
    public void Elapsed_Hours_ShouldReturnHhMmSs()
    {
        var start = new DateTime(2026, 9, 18, 10, 0, 0);

        SessionSummaryView.Elapsed(start, start.AddSeconds(3605)).Should().Be("01:00:05");
    }

    // ---- SourceLine ----

    [Fact]
    public void SourceLine_Source_ShouldRenderSourcePrefix()
    {
        SessionSummaryView.SourceLine(RelationshipSource.Source, "主线 A")
            .Should().Be("来源：主线 A");
    }

    [Fact]
    public void SourceLine_HandoffTo_ShouldRenderHandoffArrow()
    {
        SessionSummaryView.SourceLine(RelationshipSource.HandoffTo, "后继 B")
            .Should().Be("已交接 → 后继 B");
    }

    [Fact]
    public void SourceLine_MissingOtherTitle_ShouldRenderDeleted()
    {
        SessionSummaryView.SourceLine(RelationshipSource.Source, null).Should().Be("来源已删除");
        SessionSummaryView.SourceLine(RelationshipSource.HandoffTo, "").Should().Be("来源已删除");
    }

    [Fact]
    public void SourceLine_None_ShouldReturnEmpty()
    {
        SessionSummaryView.SourceLine(RelationshipSource.None, "任意").Should().BeEmpty();
    }
}
