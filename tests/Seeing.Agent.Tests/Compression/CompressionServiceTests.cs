using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Compression;
using Seeing.Session.Core;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Compression;

public class CompressionServiceTests
{
    [Fact]
    public async Task CompressAsync_ShouldSummarizeThenReplaceHistory()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        session.Messages.Add(SessionMessage.AssistantMessage("b"));
        session.Messages.Add(SessionMessage.UserMessage("c"));

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummarizeResult(
                "summary text",
                new[] { SessionMessage.UserMessage("c") },
                200));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var service = new CompressionService(
            summarizer.Object,
            sessionManager.Object,
            new CompressionOptions());

        var outcome = await service.CompressAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeTrue();
        outcome.TokensBefore.Should().BeGreaterThanOrEqualTo(1);
        outcome.TokensAfter.Should().BeGreaterThanOrEqualTo(1);
        outcome.MessagesRemoved.Should().Be(2);
        outcome.Summary.Should().Be("summary text");
        session.Messages.Count.Should().Be(1);
        session.Messages[0].Content.Should().Be("c");
        sessionManager.Verify(m => m.SaveAndNotifyAsync(session.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressAsync_WhenNoSummarizer_ShouldFailFast()
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(SessionData.Create());

        var service = new CompressionService(
            summarizer: null!,
            sessionManager.Object,
            new CompressionOptions());

        var outcome = await service.CompressAsync("s1", reason: "manual");

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain("未配置摘要器");
    }
}