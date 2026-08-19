using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Compression;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Compression;

public class LlmSummarizerTests
{
    [Fact]
    public async Task SummarizeAsync_ShouldReturnSummaryAndTrimmedMessages()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.UserMessage("问题一"),
            SessionMessage.AssistantMessage("回答一"),
            SessionMessage.UserMessage("问题二"),
        };

        var textCompletion = new Mock<ITextCompletion>();
        textCompletion.Setup(c => c.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("【摘要】这是压缩后的对话摘要");

        var summarizer = new LlmSummarizer(textCompletion.Object);
        var request = new SummarizeRequest(messages, MaxOutputTokens: 4000, KeepRecentCount: 1);

        var result = await summarizer.SummarizeAsync(request);

        result.Summary.Should().Contain("摘要");
        result.TrimmedMessages.Count.Should().Be(1);
        result.TrimmedMessages[0].Content.Should().Be("问题二");
        result.SummaryTokenCount.Should().BeGreaterThan(0);
    }
}