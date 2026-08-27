using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Compression;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Compression;

public class CompressionServiceTests
{
    [Fact]
    public async Task CompressAsync_ShouldSummarizeThenReplaceHistory()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("a"));
        session.AddMessage(SessionMessage.AssistantMessage("b"));
        session.AddMessage(SessionMessage.UserMessage("c"));

        SummarizeRequest? capturedRequest = null;
        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SummarizeRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new SummarizeResult(
                "summary text",
                new[] { SessionMessage.AssistantMessage("summary text"), SessionMessage.UserMessage("c") },
                200,
                MessagesRemoved: 2));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var service = new CompressionService(
            summarizer.Object,
            sessionManager.Object);

        var outcome = await service.CompressAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeTrue();
        outcome.TokensBefore.Should().BeGreaterThanOrEqualTo(1);
        outcome.TokensAfter.Should().BeGreaterThanOrEqualTo(1);
        outcome.MessagesRemoved.Should().Be(2);
        outcome.Summary.Should().Be("summary text");
        // 完整历史保留：被压缩部分原样保留 + 摘要消息插入其后 + 保留的最近消息
        // （被压缩消息不做标记：摘要消息的位置即压缩真相）
        session.Messages.Should().HaveCount(4);
        session.Messages[0].Content.Should().Be("a");
        session.Messages[1].Content.Should().Be("b");
        session.Messages[2].Content.Should().Be("summary text", "摘要消息应插入被压缩部分之后");
        session.Messages[2].Role.Should().Be(MessageRole.Assistant);
        session.Messages[2].IsSummary.Should().BeTrue("摘要消息应标记 IsSummary 供 UI 特殊展示");
        session.Messages[3].Content.Should().Be("c");
        // 统一消息来源：传递给 LLM 的只有摘要 + 最近消息
        var active = session.GetActiveMessages();
        active.Should().HaveCount(2);
        active[0].Content.Should().Be("summary text");
        active[1].Content.Should().Be("c");
        capturedRequest.Should().NotBeNull();
        capturedRequest!.SessionId.Should().Be(session.Id);
        capturedRequest.Reason.Should().Be("manual");
        sessionManager.Verify(m => m.SaveAndNotifyAsync(session.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressAsync_WithExistingSummary_ShouldInsertNewSummaryAtActiveStart()
    {
        // 已有旧摘要（activeStart > 0）的二次压缩：新摘要应插入被压缩段之后、保留消息之前
        var session = SessionData.Create();
        var oldSummary = SessionMessage.AssistantMessage("旧摘要");
        oldSummary.IsSummary = true;
        session.AddMessage(oldSummary);
        session.AddMessage(SessionMessage.UserMessage("a"));
        session.AddMessage(SessionMessage.AssistantMessage("b"));
        session.AddMessage(SessionMessage.UserMessage("c"));

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummarizeResult(
                "新摘要",
                new[] { SessionMessage.AssistantMessage("新摘要"), SessionMessage.UserMessage("c") },
                100,
                MessagesRemoved: 3));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var service = new CompressionService(
            summarizer.Object,
            sessionManager.Object);

        var outcome = await service.CompressAsync(session.Id, reason: "auto");

        outcome.Success.Should().BeTrue();
        // 输入 = 活跃消息（旧摘要 + 3 条），activeStart = 0；新摘要插入 0 + 3 处，保留消息 c 仍末尾
        session.Messages.Should().HaveCount(5);
        session.Messages[0].Content.Should().Be("旧摘要");
        session.Messages[3].Content.Should().Be("新摘要", "新摘要应插入被压缩段之后");
        session.Messages[3].IsSummary.Should().BeTrue();
        session.Messages[4].Content.Should().Be("c");
        // 压缩真相更新为最新摘要：活跃 = 新摘要 + 保留消息
        var active = session.GetActiveMessages();
        active.Should().HaveCount(2);
        active[0].Content.Should().Be("新摘要");
        active[1].Content.Should().Be("c");
    }

    [Fact]
    public async Task CompressAsync_WhenMessagesAppendedDuringSummarize_ShouldClampInsertIndex()
    {
        // 并发保护：摘要生成（LLM 耗时）期间后台任务追加消息时，插入索引基于调用前快照并校验边界
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("a"));
        session.AddMessage(SessionMessage.AssistantMessage("b"));

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => session.AddMessage(SessionMessage.UserMessage("并发消息")))
            .ReturnsAsync(new SummarizeResult(
                "摘要",
                new[] { SessionMessage.AssistantMessage("摘要") },
                100,
                MessagesRemoved: 5));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var service = new CompressionService(
            summarizer.Object,
            sessionManager.Object);

        var outcome = await service.CompressAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeTrue();
        // 快照 activeStart=0 + removed=5 越界（当前仅 3 条），就近回退到末尾，不抛异常、不丢消息
        session.Messages.Should().HaveCount(4);
        session.Messages[^1].Content.Should().Be("摘要");
        session.Messages[^1].IsSummary.Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_WhenNoSummarizer_ShouldFailFast()
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(SessionData.Create());

        var service = new CompressionService(
            summarizer: null!,
            sessionManager.Object);

        var outcome = await service.CompressAsync("s1", reason: "manual");

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain("未配置摘要器");
    }

    [Fact]
    public async Task CompressAsync_ShouldPersistSummaryAsAnchor_ForNextCompaction()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("a"));
        session.AddMessage(SessionMessage.AssistantMessage("b"));

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummarizeResult(
                "新摘要内容",
                new[] { SessionMessage.UserMessage("b") },
                100,
                MessagesRemoved: 1));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var service = new CompressionService(
            summarizer.Object,
            sessionManager.Object);

        var outcome = await service.CompressAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeTrue();
        session.GetContext<string>(SummarizeRequest.LastSummaryContextKey).Should().Be("新摘要内容", "本次摘要应写回会话上下文供下次锚定");
        sessionManager.Verify(m => m.SaveAndNotifyAsync(session.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressAsync_WhenActiveHasNoUserMessage_ShouldInsertNewSummaryAfterOld()
    {
        // 重复压缩无新消息（活跃 = 纯旧摘要 + 残留）：新摘要必须插入旧摘要之后成为新的压缩真相
        var session = SessionData.Create();
        var oldSummary = SessionMessage.AssistantMessage("旧摘要");
        oldSummary.IsSummary = true;
        session.AddMessage(oldSummary);
        session.AddMessage(SessionMessage.AssistantMessage("残留"));

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummarizeResult(
                "更新后摘要",
                new[] { SessionMessage.AssistantMessage("更新后摘要"), SessionMessage.AssistantMessage("残留") },
                100,
                MessagesRemoved: 1));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var service = new CompressionService(
            summarizer.Object,
            sessionManager.Object);

        var outcome = await service.CompressAsync(session.Id, reason: "auto");

        outcome.Success.Should().BeTrue();
        // activeStart=0 + removed=1 → 新摘要插入旧摘要之后
        session.Messages.Should().HaveCount(3);
        session.Messages[0].Content.Should().Be("旧摘要");
        session.Messages[1].Content.Should().Be("更新后摘要");
        session.Messages[1].IsSummary.Should().BeTrue();
        session.Messages[2].Content.Should().Be("残留");
        // 压缩真相更新：活跃以最新摘要为起点（最后一个摘要）
        var active = session.GetActiveMessages();
        active.Should().HaveCount(2);
        active[0].Content.Should().Be("更新后摘要");
    }
}