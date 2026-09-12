using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.WebUI.Components.Models;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Tests;

public class ModelPickerItemTests
{
    [Fact]
    public void Build_FilterText_ExcludesEmbeddingOnly()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["p/a"] = new ModelConfig { Id = "a", Provider = "p", Name = "A", Types = [ModelType.Text] },
            ["p/b"] = new ModelConfig { Id = "b", Provider = "p", Name = "B", Types = [ModelType.Embedding] },
            ["p/c"] = new ModelConfig { Id = "c", Provider = "p", Name = "C" } // empty Types => Text
        };

        var items = ModelPickerItem.Build(models, ModelType.Text);

        items.Select(i => i.Key).Should().BeEquivalentTo(["p/a", "p/c"]);
    }

    [Fact]
    public void Build_FilterEmbedding_OnlyEmbedding()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["p/a"] = new ModelConfig { Id = "a", Provider = "p", Types = [ModelType.Text] },
            ["p/e"] = new ModelConfig { Id = "e", Provider = "p", Types = [ModelType.Embedding] }
        };

        var items = ModelPickerItem.Build(models, ModelType.Embedding);

        items.Should().ContainSingle().Which.Key.Should().Be("p/e");
    }

    [Fact]
    public void Build_NullFilterType_ReturnsAll()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["p/a"] = new ModelConfig { Id = "a", Provider = "p", Types = [ModelType.Text] },
            ["p/e"] = new ModelConfig { Id = "e", Provider = "p", Types = [ModelType.Embedding] }
        };

        ModelPickerItem.Build(models, filterType: null).Should().HaveCount(2);
    }

    [Fact]
    public void Build_ProviderId_FiltersByProvider()
    {
        var models = new Dictionary<string, ModelConfig>
        {
            ["openai/x"] = new ModelConfig { Id = "x", Provider = "openai" },
            ["deepseek/y"] = new ModelConfig { Id = "y", Provider = "deepseek" }
        };

        var items = ModelPickerItem.Build(models, ModelType.Text, providerId: "openai");

        items.Should().ContainSingle().Which.Key.Should().Be("openai/x");
    }

    [Fact]
    public void IsFreeModel_ReadsBoolAndJsonElement()
    {
        var free = new ModelConfig
        {
            Metadata = new Dictionary<string, object?> { [ModelMetadataKeys.IsFree] = true }
        };
        var freeJson = new ModelConfig
        {
            Metadata = new Dictionary<string, object?>
            {
                [ModelMetadataKeys.IsFree] = JsonSerializer.SerializeToElement(true)
            }
        };
        var paid = new ModelConfig();

        ModelPickerItem.IsFreeModel(free).Should().BeTrue();
        ModelPickerItem.IsFreeModel(freeJson).Should().BeTrue();
        ModelPickerItem.IsFreeModel(paid).Should().BeFalse();
    }

    [Fact]
    public void From_BadgeLabel_UsesProviderAndDisplayName()
    {
        var item = ModelPickerItem.From(
            "openai/gpt-4o",
            new ModelConfig { Id = "gpt-4o", Provider = "openai", Name = "GPT-4o" });

        item.BadgeLabel.Should().Be("GPT-4o");
        item.BadgeProvider.Should().Be("openai");
        item.DisplayName.Should().Be("GPT-4o");
    }
}
