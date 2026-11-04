using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class TextCompletionServiceTests
{
    private const string ChatDefaultModel = "openai/m1";

    private static Mock<ILlmService> CreateLlm(string expectedModel = ChatDefaultModel, string content = "  hello  ")
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        llm.Setup(x => x.CompleteRawAsync(expectedModel, It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = content }
            });
        return llm;
    }

    /// <summary>chat 能力默认值是唯一回落源（旧单值 DefaultModel 已删除，决议 11）。</summary>
    private static IModelConfigManager CreateCatalog(string? chatDefault = ChatDefaultModel)
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(c => c.GetDefaultModel(LlmCapabilityKind.Chat)).Returns(chatDefault);
        return catalog.Object;
    }

    [Fact]
    public async Task CompleteAsync_WhenModelEmptyAndNoDefault_ShouldThrow()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        var svc = new TextCompletionService(
            llm.Object,
            CreateCatalog(chatDefault: null),
            NullLogger<TextCompletionService>.Instance);

        var act = () => svc.CompleteAsync("sys", "user", model: null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Complete_NoModelArg_ShouldUseChatDefault()
    {
        var llm = CreateLlm();
        var svc = new TextCompletionService(llm.Object, CreateCatalog());

        var text = await svc.CompleteAsync("sys", "user", ct: TestContext.Current.CancellationToken);

        text.Should().Be("hello");
        llm.Verify(x => x.CompleteRawAsync(ChatDefaultModel, It.Is<ChatRequest>(r =>
            r.SystemPrompt == "sys" &&
            r.Model == ChatDefaultModel &&
            r.Messages[0].Content == "user" &&
            r.MaxTokens == TextCompletionService.DefaultMaxTokens), It.IsAny<CancellationToken>()), Times.Once);
        llm.Verify(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        llm.Verify(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// 显式 model 参数优先于 chat 默认值。
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ShouldPassExplicitMaxTokens()
    {
        var llm = CreateLlm("m1", "短");
        var svc = new TextCompletionService(llm.Object, CreateCatalog());

        await svc.CompleteAsync("sys", "user", model: "m1", maxTokens: 32, TestContext.Current.CancellationToken);
        llm.Verify(x => x.CompleteRawAsync("m1", It.Is<ChatRequest>(r => r.MaxTokens == 32), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_WithMessages_ShouldPassHistory()
    {
        var llm = CreateLlm("m1", "标题");
        var svc = new TextCompletionService(llm.Object, CreateCatalog());

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "hello" }
        };
        var text = await svc.CompleteAsync("sys", messages, model: "m1", maxTokens: 32, TestContext.Current.CancellationToken);
        text.Should().Be("标题");
        llm.Verify(x => x.CompleteRawAsync(
            "m1",
            It.Is<ChatRequest>(r => r.Messages.Count == 1 && r.MaxTokens == 32),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// <c>TitleGeneration.Model</c> 不外溢到共享旁路补全：该配置已由 SessionTitleService
    /// （<c>options.Model ?? fallbackModelId</c>）消费，摘要/记忆抽取等其它 ITextCompletion 调用方
    /// 必须继续回落 chat 默认值，否则"配一个便宜标题模型"会静默改掉摘要与抽取用的模型。
    /// </summary>
    [Fact]
    public async Task Complete_TitleGenerationModelSet_ShouldNotLeakIntoBypassFallback()
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(c => c.GetDefaultModel(LlmCapabilityKind.Chat)).Returns(ChatDefaultModel);
        var llm = CreateLlm();

        var svc = new TextCompletionService(llm.Object, catalog.Object);
        var text = await svc.CompleteAsync("sys", "user", ct: TestContext.Current.CancellationToken);

        text.Should().Be("hello");
        llm.Verify(x => x.CompleteRawAsync(ChatDefaultModel, It.IsAny<ChatRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StreamCompleteAsync_ShouldPassthroughContentAndReasoning()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        llm.Setup(x => x.CompleteRawStreamAsync(ChatDefaultModel, It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns(StreamUpdates(
                new StreamUpdate { ContentDelta = "正文一" },
                new StreamUpdate { ReasoningDelta = "思考一" },
                new StreamUpdate { ContentDelta = "正文二", ReasoningDelta = "思考二" }));

        var svc = new TextCompletionService(llm.Object, CreateCatalog());

        var updates = new List<StreamUpdate>();
        await foreach (var update in svc.StreamCompleteAsync("sys", new List<ChatMessage>(), ct: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        updates.Should().HaveCount(3, "流式透传完整增量，不丢弃推理内容");
        updates[0].ContentDelta.Should().Be("正文一");
        updates[1].ReasoningDelta.Should().Be("思考一");
        updates[2].ContentDelta.Should().Be("正文二");
        updates[2].ReasoningDelta.Should().Be("思考二");
    }

    [Fact]
    public async Task StreamCompleteAsync_WhenNoDefault_ShouldThrow()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        var svc = new TextCompletionService(llm.Object, CreateCatalog(chatDefault: null));

        var act = async () =>
        {
            await foreach (var _ in svc.StreamCompleteAsync("sys", new List<ChatMessage>(), ct: TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamUpdates(params StreamUpdate[] values)
    {
        foreach (var value in values)
        {
            yield return value;
        }
    }
}
