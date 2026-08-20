using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Compression;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Compression;

public class LlmSummarizerTests
{
    private static (Mock<ITextCompletion> Mock, List<ChatMessage> Captured) SetupTextCompletion(string response = "【摘要】这是压缩后的对话摘要")
    {
        var captured = new List<ChatMessage>();
        var textCompletion = new Mock<ITextCompletion>();
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable(response))
            .Callback<string, List<ChatMessage>, string?, int?, CancellationToken>(
                (_, messages, _, _, _) => captured.AddRange(messages));
        return (textCompletion, captured);
    }

    private static async IAsyncEnumerable<StreamUpdate> AsyncEnumerable(params string[] values)
    {
        foreach (var value in values)
        {
            yield return new StreamUpdate { ContentDelta = value };
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StreamUpdate> ReasoningStreams(params string[] values)
    {
        foreach (var value in values)
        {
            yield return new StreamUpdate { ReasoningDelta = value };
        }
        await Task.CompletedTask;
    }

    private static Mock<ISessionManager> SetupSessionManager(
        SessionData session,
        out Mock<ITextCompletion> textCompletion,
        out List<ChatMessage> captured)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        (textCompletion, captured) = SetupTextCompletion();
        return sessionManager;
    }

    private static SessionData CreateSession(params SessionMessage[] messages)
    {
        var session = SessionData.Create();
        session.Messages.AddRange(messages);
        return session;
    }

    [Fact]
    public async Task SummarizeAsync_ShouldReturnSummaryAndResultMessages()
    {
        var session = CreateSession(
            SessionMessage.UserMessage("问题一"),
            SessionMessage.AssistantMessage("回答一"),
            SessionMessage.UserMessage("问题二"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        result.Summary.Should().Contain("摘要");
        result.ResultMessages.Should().HaveCount(2, "新历史 = 摘要消息 + 最后一条 user 消息");
        result.ResultMessages[0].Role.Should().Be(MessageRole.Assistant, "摘要写回为 assistant 消息");
        result.ResultMessages[0].Content.Should().Contain("摘要");
        result.ResultMessages[1].Content.Should().Be("问题二");
        result.MessagesRemoved.Should().Be(2, "3 条中压缩 2 条，保留最后一条 user 消息");
        result.SummaryTokenCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SummarizeAsync_ShouldKeepCompleteLastTurnWithToolPairing()
    {
        // 最后轮次含 tool_call 与 tool 响应的配对：必须整轮保留，防止切断配对
        var session = CreateSession(
            SessionMessage.UserMessage("问题一"),
            SessionMessage.AssistantMessage("回答一"),
            SessionMessage.UserMessage("问题二"),
            SessionMessage.AssistantMessageWithToolCalls(
                new List<SessionToolCall> { new() { Id = "call_1", Name = "search", Arguments = "{}" } }),
            SessionMessage.ToolMessage("搜索结果", "call_1"),
            SessionMessage.AssistantMessage("基于结果回答"),
            SessionMessage.UserMessage("问题三"),
            SessionMessage.AssistantMessage("回答三"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        result.MessagesRemoved.Should().Be(6, "压缩到最后一条 user（问题三）之前");
        result.ResultMessages.Should().HaveCount(3, "新历史 = 摘要 + 完整最后轮次（问题三 + 回答三）");
        result.ResultMessages[1].Content.Should().Be("问题三");
        result.ResultMessages[2].Content.Should().Be("回答三");
    }

    [Fact]
    public async Task SummarizeAsync_ShouldUseSessionSelectedModel()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        session.SelectedModel = "trip/DeepSeek-V4-Pro-discount";
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        string? capturedModel = null;
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable("摘要"))
            .Callback<string, List<ChatMessage>, string?, int?, CancellationToken>(
                (_, _, model, _, _) => capturedModel = model);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        capturedModel.Should().Be("trip/DeepSeek-V4-Pro-discount", "摘要应使用会话已选择的模型而非全局默认");
    }

    [Fact]
    public async Task SummarizeAsync_WhenNoSessionModel_ShouldPassNullModel()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        session.SelectedModel = "";
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        string? capturedModel = "sentinel";
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable("摘要"))
            .Callback<string, List<ChatMessage>, string?, int?, CancellationToken>(
                (_, _, model, _, _) => capturedModel = model);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        capturedModel.Should().BeNull("会话未选择模型时保持 null，由 ITextCompletion 回退全局默认");
    }

    [Fact]
    public async Task SummarizeAsync_ShouldPublishStreamingDeltas()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Streams(
                new StreamUpdate { ContentDelta = "第一段" },
                new StreamUpdate { ReasoningDelta = "思考一" },
                new StreamUpdate { ContentDelta = "第二段" },
                new StreamUpdate { ReasoningDelta = "思考二" }));

        var sink = new Mock<ICompactionEventSink>();
        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object,
            compactionEventSink: sink.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        result.Summary.Should().Be("第一段第二段", "摘要正文仅拼接 ContentDelta，推理不进入摘要正文");
        // 起始阶段事件 + 正文/推理各增量事件
        sink.Verify(s => s.PublishDelta(session.Id, "summarizing", null), Times.Once);
        sink.Verify(s => s.PublishDelta(session.Id, "summarizing", "第一段"), Times.Once);
        sink.Verify(s => s.PublishDelta(session.Id, "summarizing", "第二段"), Times.Once);
        sink.Verify(s => s.PublishDelta(session.Id, "summarizing", null, "思考一"), Times.Once);
        sink.Verify(s => s.PublishDelta(session.Id, "summarizing", null, "思考二"), Times.Once);
        sink.VerifyNoOtherCalls();
    }

    private static async IAsyncEnumerable<StreamUpdate> Streams(params StreamUpdate[] values)
    {
        foreach (var value in values)
        {
            yield return value;
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SummarizeAsync_WhenNoEventSink_ShouldStillWork()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);

        var result = await summarizer.SummarizeAsync(new SummarizeRequest(session.Id));

        result.Summary.Should().NotBeNullOrWhiteSpace("未配置 ICompactionEventSink 时压缩应正常执行（仅不推送进度）");
    }

    [Fact]
    public async Task SummarizeAsync_WhenNoMaxTokensConfigured_ShouldNotLimitOutput()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        int? capturedMaxTokens = 999;
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable("摘要"))
            .Callback<string, List<ChatMessage>, string?, int?, CancellationToken>(
                (_, _, _, maxTokens, _) => capturedMaxTokens = maxTokens);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        await summarizer.SummarizeAsync(request);

        capturedMaxTokens.Should().BeNull("未配置 SummaryTargetTokens 时不应限制输出，防止摘要截断导致压缩不完整");
    }

    [Fact]
    public async Task SummarizeAsync_WhenMaxTokensConfigured_ShouldPassLimit()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        int? capturedMaxTokens = null;
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable("摘要"))
            .Callback<string, List<ChatMessage>, string?, int?, CancellationToken>(
                (_, _, _, maxTokens, _) => capturedMaxTokens = maxTokens);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            options: new CompressionOptions { SummaryTargetTokens = 2000 },
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        await summarizer.SummarizeAsync(request);

        capturedMaxTokens.Should().Be(2000, "显式配置 SummaryTargetTokens 时作为输出上限传递");
    }

    [Fact]
    public async Task SummarizeAsync_ShouldPassHistoryAsMessages_WithCompactionPromptAppended()
    {
        var session = CreateSession(
            SessionMessage.UserMessage("问题一"),
            SessionMessage.AssistantMessage("回答一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out var capturedMessages);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        await summarizer.SummarizeAsync(request);

        capturedMessages.Should().HaveCount(3);
        capturedMessages[0].Role.Should().Be(ChatRole.User);
        capturedMessages[0].Content.Should().Be("问题一");
        capturedMessages[1].Role.Should().Be(ChatRole.Assistant);
        capturedMessages[1].Content.Should().Be("回答一");
        capturedMessages[2].Role.Should().Be(ChatRole.User);
        capturedMessages[2].Content.Should().Contain("创建新的锚定摘要", "压缩指令作为独立 user 消息追加，且无先前摘要时创建新摘要");
    }

    [Fact]
    public async Task SummarizeAsync_WhenPreviousSummaryInContext_ShouldRequestAnchoredUpdate()
    {
        var session = CreateSession(SessionMessage.UserMessage("新消息"));
        session.SetContext(SummarizeRequest.LastSummaryContextKey, "旧摘要内容");
        var sessionManager = SetupSessionManager(session, out var textCompletion, out var capturedMessages);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        await summarizer.SummarizeAsync(request);

        var prompt = capturedMessages[^1].Content;
        prompt.Should().Contain("更新锚定摘要", "有先前摘要时应请求更新合并");
        prompt.Should().Contain("<previous-summary>");
        prompt.Should().Contain("旧摘要内容");
        prompt.Should().Contain("</previous-summary>");
    }

    [Fact]
    public async Task SummarizeAsync_ShouldUseSummaryAgentSystemPrompt_WhenAvailable()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        string? capturedSystemPrompt = null;
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable("摘要"))
            .Callback<string, List<ChatMessage>, string?, int?, CancellationToken>(
                (systemPrompt, _, _, _, _) => capturedSystemPrompt = systemPrompt);

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.GetAgentAsync("summary"))
            .ReturnsAsync(new AgentDefinition
            {
                Name = "summary",
                SystemPrompt = "你是一个会话压缩助手。将对话历史压缩为结构化摘要，保留关键信息。"
            });

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            agentRegistry: agentRegistry.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        capturedSystemPrompt.Should().Contain("会话压缩助手", "应复用内置 summary Agent 的系统提示词");
    }

    [Fact]
    public async Task SummarizeAsync_ShouldFallbackToDefaultPrompt_WhenSummaryAgentMissing()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        string? capturedSystemPrompt = null;
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable("摘要"))
            .Callback<string, List<ChatMessage>, string?, int?, CancellationToken>(
                (systemPrompt, _, _, _, _) => capturedSystemPrompt = systemPrompt);

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.GetAgentAsync("summary"))
            .ReturnsAsync((AgentDefinition?)null);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            agentRegistry: agentRegistry.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        capturedSystemPrompt.Should().Contain("会话压缩助手", "summary Agent 不存在时回退内置默认提示词");
    }

    [Fact]
    public async Task SummarizeAsync_WhenNoSessionManager_ShouldThrow()
    {
        var textCompletion = SetupTextCompletion().Mock;

        var summarizer = new LlmSummarizer(textCompletion.Object);
        var request = new SummarizeRequest("s1");

        var act = () => summarizer.SummarizeAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未配置会话管理器*");
    }

    [Fact]
    public async Task SummarizeAsync_WhenNoUserMessage_ShouldStillProduceNewTruth()
    {
        // 活跃消息无 user（压缩后无新消息再次压缩）：至少跳过活跃段首（旧摘要），
        // 保证新摘要插入旧摘要之后成为新的压缩真相，避免每次重复全量摘要却永不生效
        var oldSummary = SessionMessage.AssistantMessage("旧摘要");
        oldSummary.IsSummary = true;
        var session = CreateSession(
            oldSummary,
            SessionMessage.AssistantMessage("残留回复"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var result = await summarizer.SummarizeAsync(request);

        result.MessagesRemoved.Should().Be(1, "无 user 消息时应至少压缩活跃段首（旧摘要），使新摘要成为真相");
        result.ResultMessages.Should().HaveCount(2);
        result.ResultMessages[0].Content.Should().Be("【摘要】这是压缩后的对话摘要");
        result.ResultMessages[1].Content.Should().Be("残留回复");
    }

    [Fact]
    public async Task SummarizeAsync_WhenStreamFails_ShouldPropagateException()
    {
        var session = CreateSession(SessionMessage.UserMessage("消息一"));
        var sessionManager = SetupSessionManager(session, out var textCompletion, out _);
        textCompletion.Setup(c => c.StreamCompleteAsync(
                It.IsAny<string>(), It.IsAny<List<ChatMessage>>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ThrowStream());

        var summarizer = new LlmSummarizer(
            textCompletion.Object,
            sessionManager: sessionManager.Object);
        var request = new SummarizeRequest(session.Id);

        var act = () => summarizer.SummarizeAsync(request);

        // 流式失败原样传播：CompressionService 统一捕获并转为失败结果
        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("上游服务不可用");
    }

    private static async IAsyncEnumerable<StreamUpdate> ThrowStream()
    {
        throw new HttpRequestException("上游服务不可用");
        yield break;
    }
}