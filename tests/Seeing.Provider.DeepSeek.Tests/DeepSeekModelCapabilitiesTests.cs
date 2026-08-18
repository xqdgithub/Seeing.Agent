using FluentAssertions;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekModelCapabilitiesTests
{
    [Fact]
    public void Apply_KnownModel_OverlaysLimitAndName()
    {
        var listed = new ModelConfig
        {
            Id = "deepseek-chat",
            Name = "deepseek-chat",
            Provider = "deepseek"
        };

        var enriched = DeepSeekModelCapabilities.Apply(listed);

        enriched.Id.Should().Be("deepseek-chat");
        enriched.Name.Should().Be("DeepSeek Chat");
        enriched.Provider.Should().Be("deepseek");
        enriched.Limit.Context.Should().Be(1_000_000);
        enriched.Limit.Output.Should().Be(384_000);
        enriched.Types.Should().Contain(ModelType.Text);
        enriched.Modalities.Input.Should().Contain("text");
    }

    [Fact]
    public void Apply_Reasoner_EnablesThinking()
    {
        var enriched = DeepSeekModelCapabilities.Apply(new ModelConfig
        {
            Id = "deepseek-reasoner",
            Provider = "deepseek"
        });

        enriched.Options.Should().NotBeNull();
        enriched.Options!.Thinking.Should().NotBeNull();
        enriched.Options.Thinking!.Type.Should().Be("enabled");
        enriched.Limit.Context.Should().Be(1_000_000);
    }

    [Fact]
    public void Apply_V4Models_HaveMillionContext()
    {
        foreach (var id in new[] { "deepseek-v4-flash", "deepseek-v4-pro" })
        {
            var enriched = DeepSeekModelCapabilities.Apply(new ModelConfig { Id = id });
            enriched.Limit.Context.Should().Be(1_000_000);
            enriched.Limit.Output.Should().Be(384_000);
        }
    }

    [Fact]
    public void Apply_UnknownModel_KeepsDefaultLimit()
    {
        var listed = new ModelConfig
        {
            Id = "deepseek-unknown-future",
            Name = "deepseek-unknown-future",
            Provider = "deepseek"
        };

        var enriched = DeepSeekModelCapabilities.Apply(listed);

        enriched.Id.Should().Be("deepseek-unknown-future");
        enriched.Limit.Context.Should().Be(4096);
        enriched.Limit.Output.Should().Be(4096);
    }

    [Fact]
    public void Apply_IsCaseInsensitive()
    {
        var enriched = DeepSeekModelCapabilities.Apply(new ModelConfig
        {
            Id = "DeepSeek-Chat"
        });

        enriched.Limit.Context.Should().Be(1_000_000);
        enriched.Name.Should().Be("DeepSeek Chat");
    }
}
