using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
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
    public async Task CompleteAsync_ShouldDelegateToLlmService()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(x => x.CompleteAsync("m1", It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "  hello  " }
            });

        var options = new SeeingAgentOptions { DefaultModel = "m1" };
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);
        var svc = new TextCompletionService(
            llm.Object,
            optionsMonitor);

        var text = await svc.CompleteAsync("sys", "user");
        text.Should().Be("hello");
        llm.Verify(x => x.CompleteAsync("m1", It.Is<ChatRequest>(r =>
            r.SystemPrompt == "sys" &&
            r.Messages[0].Content == "user" &&
            r.MaxTokens == TextCompletionService.DefaultMaxTokens), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_ShouldPassExplicitMaxTokens()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(x => x.CompleteAsync("m1", It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "短" }
            });

        var options = new SeeingAgentOptions { DefaultModel = "m1" };
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);
        var svc = new TextCompletionService(llm.Object, optionsMonitor);

        await svc.CompleteAsync("sys", "user", model: "m1", maxTokens: 32);
        llm.Verify(x => x.CompleteAsync("m1", It.Is<ChatRequest>(r => r.MaxTokens == 32), It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class OptionsProviderEndpointLookupTests
{
    [Fact]
    public void TryGet_WhenMissing_ShouldReturnFalse()
    {
        var options = new SeeingAgentOptions();
        var lookup = new OptionsProviderEndpointLookup(Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options));
        lookup.TryGet("openai", out var ep).Should().BeFalse();
        ep.Should().BeNull();
    }

    [Fact]
    public void TryGet_WhenPresent_ShouldMapEndpoint()
    {
        var opts = new SeeingAgentOptions
        {
            Providers =
            {
                ["openai"] = new ProviderConfig { BaseUrl = "https://api.example/v1", ApiKey = "k" }
            }
        };
        var lookup = new OptionsProviderEndpointLookup(Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == opts));
        lookup.TryGet("openai", out var ep).Should().BeTrue();
        ep!.BaseUrl.Should().Be("https://api.example/v1");
        ep.ApiKey.Should().Be("k");
    }
}
