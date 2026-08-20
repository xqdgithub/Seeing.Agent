using FluentAssertions;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class TextCompletionServiceTests
{
    [Fact]
    public async Task CompleteAsync_WhenModelEmptyAndNoDefault_ShouldThrow()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        var options = new SeeingAgentOptions { DefaultModel = null };
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);
        var svc = new TextCompletionService(
            llm.Object,
            optionsMonitor,
            NullLogger<TextCompletionService>.Instance);

        var act = () => svc.CompleteAsync("sys", "user", model: null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CompleteAsync_ShouldUseCompleteRawAsync_WithoutHooks()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        llm.Setup(x => x.CompleteRawAsync("m1", It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "  hello  " }
            });

        var options = new SeeingAgentOptions { DefaultModel = "m1" };
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);
        var svc = new TextCompletionService(llm.Object, optionsMonitor);

        var text = await svc.CompleteAsync("sys", "user");
        text.Should().Be("hello");
        llm.Verify(x => x.CompleteRawAsync("m1", It.Is<ChatRequest>(r =>
            r.SystemPrompt == "sys" &&
            r.Messages[0].Content == "user" &&
            r.MaxTokens == TextCompletionService.DefaultMaxTokens), It.IsAny<CancellationToken>()), Times.Once);
        llm.Verify(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        llm.Verify(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteAsync_ShouldPassExplicitMaxTokens()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        llm.Setup(x => x.CompleteRawAsync("m1", It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "短" }
            });

        var options = new SeeingAgentOptions { DefaultModel = "m1" };
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);
        var svc = new TextCompletionService(llm.Object, optionsMonitor);

        await svc.CompleteAsync("sys", "user", model: "m1", maxTokens: 32);
        llm.Verify(x => x.CompleteRawAsync("m1", It.Is<ChatRequest>(r => r.MaxTokens == 32), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_WithMessages_ShouldPassHistory()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        llm.Setup(x => x.CompleteRawAsync("m1", It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "标题" }
            });

        var options = new SeeingAgentOptions { DefaultModel = "m1" };
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);
        var svc = new TextCompletionService(llm.Object, optionsMonitor);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "hello" }
        };
        var text = await svc.CompleteAsync("sys", messages, model: "m1", maxTokens: 32);
        text.Should().Be("标题");
        llm.Verify(x => x.CompleteRawAsync(
            "m1",
            It.Is<ChatRequest>(r => r.Messages.Count == 1 && r.MaxTokens == 32),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StreamCompleteAsync_ShouldPassthroughContentAndReasoning()
    {
        var llm = new Mock<ILlmService>(MockBehavior.Strict);
        llm.Setup(x => x.CompleteRawStreamAsync("m1", It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .Returns(StreamUpdates(
                new StreamUpdate { ContentDelta = "正文一" },
                new StreamUpdate { ReasoningDelta = "思考一" },
                new StreamUpdate { ContentDelta = "正文二", ReasoningDelta = "思考二" }));

        var options = new SeeingAgentOptions { DefaultModel = "m1" };
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);
        var svc = new TextCompletionService(llm.Object, optionsMonitor);

        var updates = new List<StreamUpdate>();
        await foreach (var update in svc.StreamCompleteAsync("sys", new List<ChatMessage>()))
        {
            updates.Add(update);
        }

        updates.Should().HaveCount(3, "流式透传完整增量，不丢弃推理内容");
        updates[0].ContentDelta.Should().Be("正文一");
        updates[1].ReasoningDelta.Should().Be("思考一");
        updates[2].ContentDelta.Should().Be("正文二");
        updates[2].ReasoningDelta.Should().Be("思考二");
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamUpdates(params StreamUpdate[] values)
    {
        foreach (var value in values)
        {
            yield return value;
        }
    }
}

public class OptionsProviderEndpointLookupTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "endpoint-lookup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TryGet_WhenMissing_ShouldReturnFalse()
    {
        var configManager = await CreateConfigManagerAsync();
        var lookup = new OptionsProviderEndpointLookup(
            configManager,
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance));
        lookup.TryGet("openai", out var ep).Should().BeFalse();
        ep.Should().BeNull();
    }

    [Fact]
    public async Task TryGet_WhenPresent_ShouldMapEndpoint()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig { BaseUrl = "https://api.example/v1", ApiKey = "k" }
        };
        var configManager = await CreateConfigManagerAsync(providers);
        var lookup = new OptionsProviderEndpointLookup(
            configManager,
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance));
        lookup.TryGet("openai", out var ep).Should().BeTrue();
        ep!.BaseUrl.Should().Be("https://api.example/v1");
        ep.ApiKey.Should().Be("k");
    }

    [Fact]
    public async Task TryGet_WhenRegisteredProviderExposesEndpoint_ShouldPreferProvider()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["extension"] = new ProviderConfig { BaseUrl = "https://fallback", ApiKey = "fallback-key" }
        };
        var configManager = await CreateConfigManagerAsync(providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new Mock<ILlmProvider>();
        provider.SetupGet(item => item.Id).Returns("extension");
        provider.As<IProviderEndpointInfo>().SetupGet(item => item.BaseUrl).Returns("https://provider");
        provider.As<IProviderEndpointInfo>().SetupGet(item => item.ApiKey).Returns("provider-key");
        registry.Register(provider.Object, "extension-id");
        var lookup = new OptionsProviderEndpointLookup(configManager, registry);

        lookup.TryGet("extension", out var endpoint).Should().BeTrue();

        endpoint!.BaseUrl.Should().Be("https://provider");
        endpoint.ApiKey.Should().Be("provider-key");
    }

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(
        Dictionary<string, ProviderConfig>? providers = null)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        if (providers is { Count: > 0 })
        {
            await File.WriteAllTextAsync(
                Path.Combine(userSeeingDirectory, "providers.json"),
                JsonSerializer.Serialize(providers));
        }

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance);
        await manager.LoadAsync();
        return manager;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
