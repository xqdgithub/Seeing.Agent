using FluentAssertions;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ModelTypeFilterTests
{
    [Fact]
    public void GetEffectiveTypes_WhenEmpty_ShouldBeTextOnly()
    {
        var types = ModelTypeRules.GetEffectiveTypes(new ModelConfig { Id = "m", Provider = "p" });
        types.Should().Equal(ModelType.Text);
    }

    [Fact]
    public void GetEffectiveTypes_WhenExplicit_ShouldReturnAsIs()
    {
        var types = ModelTypeRules.GetEffectiveTypes(new ModelConfig
        {
            Id = "e",
            Provider = "p",
            Types = [ModelType.Embedding, ModelType.Rerank]
        });
        types.Should().Equal(ModelType.Embedding, ModelType.Rerank);
    }

    [Fact]
    public void Matches_DefaultText_ShouldExcludeEmbeddingOnly()
    {
        var gpt = new ModelConfig { Id = "gpt", Provider = "openai", Types = [ModelType.Text] };
        var emb = new ModelConfig { Id = "emb", Provider = "openai", Types = [ModelType.Embedding] };
        var legacy = new ModelConfig { Id = "legacy", Provider = "openai" };

        ModelTypeRules.Matches(gpt, ModelType.Text).Should().BeTrue();
        ModelTypeRules.Matches(legacy, ModelType.Text).Should().BeTrue();
        ModelTypeRules.Matches(emb, ModelType.Text).Should().BeFalse();
        ModelTypeRules.Matches(emb, ModelType.Embedding).Should().BeTrue();
    }

    [Fact]
    public void Matches_MultiLabel_ShouldHitBoth()
    {
        var multi = new ModelConfig
        {
            Id = "multi",
            Provider = "openai",
            Types = [ModelType.Text, ModelType.Image]
        };
        ModelTypeRules.Matches(multi, ModelType.Text).Should().BeTrue();
        ModelTypeRules.Matches(multi, ModelType.Image).Should().BeTrue();
        ModelTypeRules.Matches(multi, ModelType.Embedding).Should().BeFalse();
    }

    [Fact]
    public void FilterByType_WithProvider_ShouldFilter()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["openai/a"] = new() { Id = "a", Provider = "openai", Types = [ModelType.Text] },
            ["anthropic/b"] = new() { Id = "b", Provider = "anthropic", Types = [ModelType.Text] },
            ["openai/emb"] = new() { Id = "emb", Provider = "openai", Types = [ModelType.Embedding] }
        };

        var textOpenai = ModelTypeRules.FilterByType(models, ModelType.Text, "openai");
        textOpenai.Keys.Should().BeEquivalentTo("openai/a");

        var allText = ModelTypeRules.FilterByType(models, ModelType.Text);
        allText.Keys.Should().BeEquivalentTo("openai/a", "anthropic/b");
    }

    [Theory]
    [InlineData("openai/gpt", true)]
    [InlineData("openai/emb", false)]
    [InlineData("openai/legacy", true)]
    public void CanSetAsDefault_RequiresText(string modelId, bool expected)
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["openai/gpt"] = new() { Id = "gpt", Provider = "openai", Types = [ModelType.Text] },
            ["openai/emb"] = new() { Id = "emb", Provider = "openai", Types = [ModelType.Embedding] },
            ["openai/legacy"] = new() { Id = "legacy", Provider = "openai" }
        };

        ModelTypeRules.Matches(models[modelId], ModelType.Text).Should().Be(expected);
    }
}
