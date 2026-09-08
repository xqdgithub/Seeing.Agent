using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Summarization;

public class SummarizerContractTests
{
    [Fact]
    public void SummarizeRequest_ShouldCarrySessionIdAndReason()
    {
        var request = new SummarizeRequest("s1", Reason: "manual");

        request.SessionId.Should().Be("s1");
        request.Reason.Should().Be("manual");
    }

    [Fact]
    public void SummarizeRequest_Reason_ShouldDefaultToAuto()
    {
        var request = new SummarizeRequest("s1");

        request.Reason.Should().Be("auto");
    }

    [Fact]
    public void SummarizeRequest_ShouldExposeLastSummaryContextKey()
    {
        SummarizeRequest.LastSummaryContextKey.Should().Be("LastCompactionSummary");
    }

    [Fact]
    public void SummarizeResult_ShouldCarrySummaryAndResultMessages()
    {
        var resultMessages = new List<SessionMessage>();
        var result = new SummarizeResult("summary", resultMessages, SummaryTokenCount: 300, MessagesRemoved: 5);

        result.Summary.Should().Be("summary");
        result.ResultMessages.Should().BeSameAs(resultMessages);
        result.SummaryTokenCount.Should().Be(300);
        result.MessagesRemoved.Should().Be(5);
    }
}