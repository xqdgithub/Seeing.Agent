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
        var svc = new TextCompletionService(
            llm.Object,
            Options.Create(new SeeingAgentOptions { DefaultModel = null }),
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

        var svc = new TextCompletionService(
            llm.Object,
            Options.Create(new SeeingAgentOptions { DefaultModel = "m1" }));

        var text = await svc.CompleteAsync("sys", "user");
        text.Should().Be("hello");
        llm.Verify(x => x.CompleteAsync("m1", It.Is<ChatRequest>(r =>
            r.SystemPrompt == "sys" && r.Messages[0].Content == "user"), It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class OptionsProviderEndpointLookupTests
{
    [Fact]
    public void TryGet_WhenMissing_ShouldReturnFalse()
    {
        var lookup = new OptionsProviderEndpointLookup(Options.Create(new SeeingAgentOptions()));
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
        var lookup = new OptionsProviderEndpointLookup(Options.Create(opts));
        lookup.TryGet("openai", out var ep).Should().BeTrue();
        ep!.BaseUrl.Should().Be("https://api.example/v1");
        ep.ApiKey.Should().Be("k");
    }
}
