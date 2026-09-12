using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekModelCapabilitiesTests
{
    [Fact]
    public void Apply_V4Flash_OverlaysLimitNameAndThinking()
    {
        var listed = new ModelConfig
        {
            Id = "deepseek-v4-flash",
            Name = "deepseek-v4-flash",
            Provider = "deepseek"
        };

        var enriched = DeepSeekModelCapabilities.Apply(listed);

        enriched.Id.Should().Be("deepseek-v4-flash");
        enriched.Name.Should().Be("DeepSeek V4 Flash");
        enriched.Provider.Should().Be("deepseek");
        enriched.Limit.Context.Should().Be(1_000_000);
        enriched.Limit.Output.Should().Be(384_000);
        enriched.Types.Should().Contain(ModelType.Text);
        enriched.Options!.Thinking!.Supported.Should().BeTrue();
        enriched.Options.Thinking.Interleaved.Should().Be("reasoning_content");
        enriched.Options.Thinking.Levels!.Select(l => l.Key)
            .Should().Contain(["disabled", "high", "max"]);
    }

    [Fact]
    public void Apply_V4Models_HaveMillionContext()
    {
        foreach (var id in new[] { "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-flash" })
        {
            var enriched = DeepSeekModelCapabilities.Apply(new ModelConfig { Id = id });
            enriched.Limit.Context.Should().Be(1_000_000);
            enriched.Limit.Output.Should().Be(384_000);
            enriched.Options!.Thinking!.Supported.Should().BeTrue();
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
            Id = "DeepSeek-V4-Flash"
        });

        enriched.Limit.Context.Should().Be(1_000_000);
        enriched.Name.Should().Be("DeepSeek V4 Flash");
    }
}
