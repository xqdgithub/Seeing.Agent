using FluentAssertions;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ModelRefTests
{
    private static readonly string[] Providers = ["openai", "anthropic", "siliconflow"];

    [Theory]
    [InlineData("openai/gpt-4o", "openai", "gpt-4o")]
    [InlineData("siliconflow/Qwen/Qwen3-VL-Embedding-8B", "siliconflow", "Qwen/Qwen3-VL-Embedding-8B")]
    [InlineData("Qwen/Qwen3-VL-Embedding-8B", null, "Qwen/Qwen3-VL-Embedding-8B")]
    [InlineData("gpt-4o-mini", null, "gpt-4o-mini")]
    [InlineData("anthropic/claude-3-5-sonnet", "anthropic", "claude-3-5-sonnet")]
    public void Parse_KnownProviderOnly_ShouldSplit(
        string input, string? expectedProvider, string expectedModel)
    {
        var (provider, model) = ModelRef.Parse(input, Providers);
        provider.Should().Be(expectedProvider);
        model.Should().Be(expectedModel);
    }

    [Fact]
    public void Format_WithSlashInModelId_ShouldKeepModelIntact()
    {
        ModelRef.Format("siliconflow", "Qwen/Qwen3-VL-Embedding-8B")
            .Should().Be("siliconflow/Qwen/Qwen3-VL-Embedding-8B");
    }

    [Fact]
    public void Format_ThenParse_ShouldRoundTrip()
    {
        var formatted = ModelRef.Format("siliconflow", "Qwen/Qwen3-VL-Embedding-8B");
        var (provider, model) = ModelRef.Parse(formatted, Providers);
        provider.Should().Be("siliconflow");
        model.Should().Be("Qwen/Qwen3-VL-Embedding-8B");
    }
}
