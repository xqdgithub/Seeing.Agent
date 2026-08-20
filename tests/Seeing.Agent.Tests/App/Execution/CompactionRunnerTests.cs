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
        sessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        return (new CompressionService(summarizer.Object, sessionManager.Object), summarizer, sessionManager);
    }

    [Fact]
    public async Task RunAsync_ShouldPublishStartedBeforeCompressionAndCompletedAfter()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        var (compression, _, sessionManager) = CreateCompression(session);

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

        var runner = new CompactionRunner(compression, publisher.Object, sessionManager.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeTrue();
        events[0].Should().Be("started", "Started 必须最先发布：UI 进度态依赖它承载后续 Delta");
        events.Should().Contain("completed");
        events.IndexOf("started").Should().BeLessThan(events.IndexOf("completed"));
        publisher.Verify(p => p.Publish(session.Id, It.IsAny<CompactionStartedEvent>()), Times.Once);
        publisher.Verify(p => p.Publish(session.Id, It.IsAny<CompactionCompletedEvent>()), Times.Once);
        session.Messages.Should().NotContain(m => m.Role == MessageRole.System);
    }

    [Fact]
    public async Task RunAsync_WhenCompressionFails_ShouldPublishFailedAfterStarted()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        var (compression, _, sessionManager) = CreateCompression(session, fail: true);

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

        var runner = new CompactionRunner(compression, publisher.Object, sessionManager.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeFalse();
        events.Should().Equal(new[] { "started", "failed" }, "失败路径事件序列为 Started → Failed");
        publisher.Verify(p => p.Publish(session.Id, It.Is<CompactionFailedEvent>(e => !string.IsNullOrEmpty(e.ErrorMessage))), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenCompressionFails_ShouldWriteSystemMessageAndPersist()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        var (compression, _, sessionManager) = CreateCompression(session, fail: true);

        var runner = new CompactionRunner(compression, Mock.Of<IExecutionEventPublisher>(), sessionManager.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeFalse();
        var systemMessage = session.Messages.Should().ContainSingle(m => m.Role == MessageRole.System).Subject;
        systemMessage.Content.Should().Contain("压缩失败");
        systemMessage.Content.Should().Contain("上游模型不可用");
        sessionManager.Verify(m => m.SaveAndNotifyAsync(session.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenSessionMissing_ShouldPublishFailedWithoutWriting()
    {
        var session = SessionData.Create();
        var (compression, _, sessionManager) = CreateCompression(session, fail: true);
        sessionManager.Setup(m => m.Get(session.Id)).Returns((SessionData?)null);

        var runner = new CompactionRunner(compression, Mock.Of<IExecutionEventPublisher>(), sessionManager.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeFalse();
        sessionManager.Verify(m => m.SaveAndNotifyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenCompressionFails_ShouldPersistBeforePublishingFailedEvent()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        var (compression, _, sessionManager) = CreateCompression(session, fail: true);

        var order = new List<string>();
        sessionManager.Setup(m => m.SaveAndNotifyAsync(session.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("save"))
            .Returns(Task.CompletedTask);
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(session.Id, It.IsAny<CompactionFailedEvent>()))
            .Callback(() => order.Add("publish"));

        var runner = new CompactionRunner(compression, publisher.Object, sessionManager.Object);

        await runner.RunAsync(session.Id, reason: "manual");

        order.Should().Equal(new[] { "save", "publish" }, "先写会话再发布事件：UI 收到事件时读到已写入的 SessionData");
        sessionManager.Verify(m => m.SaveAndNotifyAsync(session.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.Publish(session.Id, It.IsAny<CompactionFailedEvent>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenCompressionFailsWithEmptyError_ShouldWriteFallbackMessage()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(""));
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        var compression = new CompressionService(summarizer.Object, sessionManager.Object);

        var runner = new CompactionRunner(compression, Mock.Of<IExecutionEventPublisher>(), sessionManager.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeFalse();
        var systemMessage = session.Messages.Should().ContainSingle(m => m.Role == MessageRole.System).Subject;
        systemMessage.Content.Should().Be("压缩失败");
    }

    [Fact]
    public async Task RunAsync_WhenCompressionFailsRepeatedly_ShouldReplacePreviousFailureMessage()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        session.Messages.Add(SessionMessage.SystemMessage("压缩失败: 第一次错误"));
        var (compression, _, sessionManager) = CreateCompression(session, fail: true);

        var runner = new CompactionRunner(compression, Mock.Of<IExecutionEventPublisher>(), sessionManager.Object);

        var outcome = await runner.RunAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeFalse();
        var failureMessages = session.Messages.Where(m => m.Role == MessageRole.System).ToList();
        failureMessages.Should().HaveCount(1);
        failureMessages[0].Content.Should().Contain("上游模型不可用");
    }
}
