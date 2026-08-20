using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.App.Execution;
using Seeing.Agent.Compression;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

public class CompactionRunnerTests
{
    private static (CompressionService Service, Mock<ISummarizer> Summarizer, Mock<ISessionManager> SessionManager) CreateCompression(
        SessionData session, bool fail = false)
    {
        var summarizer = new Mock<ISummarizer>();
        if (fail)
        {
            summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("上游模型不可用"));
        }
        else
        {
            summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SummarizeResult(
                    "摘要",
                    new[] { SessionMessage.AssistantMessage("摘要") },
                    10,
                    MessagesRemoved: 0));
        }

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        return (new CompressionService(summarizer.Object, sessionManager.Object), summarizer, sessionManager);
    }

    [Fact]
    public async Task RunAsync_ShouldPublishStartedBeforeCompressionAndCompletedAfter()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        var (compression, _, _) = CreateCompression(session);

        var events = new List<string>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(session.Id, It.IsAny<IMessageEvent>()))
            .Callback<string, IMessageEvent>((_, e) =>
            {
                events.Add(e switch
                {
                    CompactionStartedEvent => "started",
                    CompactionCompletedEvent => "completed",
                    CompactionFailedEvent => "failed",
                    _ => "other"
                });
            });

        var runner = new CompactionRunner(compression, publisher.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeTrue();
        events[0].Should().Be("started", "Started 必须最先发布：UI 进度态依赖它承载后续 Delta");
        events.Should().Contain("completed");
        events.IndexOf("started").Should().BeLessThan(events.IndexOf("completed"));
        publisher.Verify(p => p.Publish(session.Id, It.IsAny<CompactionStartedEvent>()), Times.Once);
        publisher.Verify(p => p.Publish(session.Id, It.IsAny<CompactionCompletedEvent>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenCompressionFails_ShouldPublishFailedAfterStarted()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        var (compression, _, _) = CreateCompression(session, fail: true);

        var events = new List<string>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(session.Id, It.IsAny<IMessageEvent>()))
            .Callback<string, IMessageEvent>((_, e) =>
            {
                events.Add(e switch
                {
                    CompactionStartedEvent => "started",
                    CompactionFailedEvent => "failed",
                    _ => "other"
                });
            });

        var runner = new CompactionRunner(compression, publisher.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeFalse();
        events.Should().Equal(new[] { "started", "failed" }, "失败路径事件序列为 Started → Failed");
        publisher.Verify(p => p.Publish(session.Id, It.Is<CompactionFailedEvent>(e => !string.IsNullOrEmpty(e.ErrorMessage))), Times.Once);
    }
}