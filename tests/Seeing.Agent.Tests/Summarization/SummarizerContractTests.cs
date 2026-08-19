using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Summarization;

public class SummarizerContractTests
{
    [Fact]
    public void SummarizeRequest_ShouldCarryMessagesAndOptions()
    {
        var messages = new List<SessionMessage> { SessionMessage.UserMessage("hi") };
        var request = new SummarizeRequest(messages, MaxOutputTokens: 2000, KeepRecentCount: 10);

        request.Messages.Should().BeEquivalentTo(messages);
        request.MaxOutputTokens.Should().Be(2000);
        request.KeepRecentCount.Should().Be(10);
    }

    [Fact]
    public void SummarizeResult_ShouldCarrySummaryAndTrimmedList()
    {
        var trimmed = new List<SessionMessage>();
        var result = new SummarizeResult("summary", trimmed, SummaryTokenCount: 300);

        result.Summary.Should().Be("summary");
        result.TrimmedMessages.Should().BeSameAs(trimmed);
        result.SummaryTokenCount.Should().Be(300);
    }
}
