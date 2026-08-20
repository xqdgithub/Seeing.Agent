using FluentAssertions;
using Seeing.Provider.OpenCodeZen;
using Xunit;

namespace Seeing.Provider.OpenCodeZen.Tests;

public class OpenCodeZenModelCatalogTests
{
    [Theory]
    [InlineData("mimo-v2.5-free", true)]
    [InlineData("hy3-free", true)]
    [InlineData("nemotron-3-ultra-free", true)]
    [InlineData("big-pickle", true)]
    [InlineData("muse-spark-1.2-contributor-free", true)]
    [InlineData("NEMOTRON-3-ULTRA-FREE", true)]
    [InlineData("deepseek-v4-pro", false)]
    [InlineData("gpt-5.6-luna", false)]
    public void IsFreeModel_DetectsFreeModels(string id, bool expected)
        => OpenCodeZenModelCatalog.IsFreeModel(id).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void IsFreeModel_BlankId_ReturnsFalse(string? id)
        => OpenCodeZenModelCatalog.IsFreeModel(id!).Should().BeFalse();

    [Fact]
    public void ApplyPreset_KnownFreeModel_OverlaysCapabilities()
    {
        var model = new OpenCodeZenModel
        {
            Id = "nemotron-3-ultra-free",
            Name = "nemotron-3-ultra-free",
            IsFree = true
        };

        var enriched = OpenCodeZenModelCatalog.ApplyPreset(model);

        enriched.IsFree.Should().BeTrue();
        enriched.Context.Should().Be(200_000);
        enriched.Output.Should().Be(65_536);
    }

    [Fact]
    public void ApplyPreset_UnknownModel_KeepsDefaults()
    {
        var model = new OpenCodeZenModel
        {
            Id = "future-model-free",
            Name = "future-model-free",
            IsFree = true
        };

        var enriched = OpenCodeZenModelCatalog.ApplyPreset(model);

        enriched.Context.Should().Be(OpenCodeZenModelCatalog.DefaultContext);
        enriched.Output.Should().Be(OpenCodeZenModelCatalog.DefaultOutput);
    }

    [Fact]
    public void ApplyOverrides_UserOverrideTakesPriority()
    {
        var model = new OpenCodeZenModel
        {
            Id = "big-pickle",
            Name = "big-pickle",
            IsFree = true
        };
        var overrides = new Dictionary<string, ModelCapabilityOverride>
        {
            ["big-pickle"] = new() { Context = 999_999, Output = 1_234 }
        };

        var enriched = OpenCodeZenModelCatalog.ApplyOverrides(model, overrides);

        enriched.Context.Should().Be(999_999);
        enriched.Output.Should().Be(1_234);
    }

    [Fact]
    public void ApplyOverrides_CaseInsensitive()
    {
        var model = new OpenCodeZenModel { Id = "gpt-5.6-luna", Name = "gpt-5.6-luna" };
        var overrides = new Dictionary<string, ModelCapabilityOverride>
        {
            ["GPT-5.6-LUNA"] = new() { Context = 300_000, Output = 90_000 }
        };

        var enriched = OpenCodeZenModelCatalog.ApplyOverrides(model, overrides);

        enriched.Context.Should().Be(300_000);
        enriched.Output.Should().Be(90_000);
    }

    [Fact]
    public void ApplyOverrides_NoMatch_KeepsModel()
    {
        var model = new OpenCodeZenModel { Id = "deepseek-v4-pro", Name = "deepseek-v4-pro" };

        var enriched = OpenCodeZenModelCatalog.ApplyOverrides(model, null);

        enriched.Should().BeSameAs(model);
        enriched.Context.Should().Be(OpenCodeZenModelCatalog.DefaultContext);
    }

    [Fact]
    public void ApplyOverrides_IsFreeOverride_MarksModelAsFree()
    {
        // 未带 -free 后缀的新免费模型：用户可通过覆盖手动标记为免费
        var model = new OpenCodeZenModel
        {
            Id = "brand-new-freebie",
            Name = "brand-new-freebie",
            IsFree = false
        };
        var overrides = new Dictionary<string, ModelCapabilityOverride>
        {
            ["brand-new-freebie"] = new() { IsFree = true }
        };

        var enriched = OpenCodeZenModelCatalog.ApplyOverrides(model, overrides);

        enriched.IsFree.Should().BeTrue();
        enriched.InputPrice.Should().Be(0);
        enriched.OutputPrice.Should().Be(0);
    }

    [Fact]
    public void ApplyOverrides_IsFreeOverrideNull_KeepsOriginalJudgement()
    {
        var free = new OpenCodeZenModel { Id = "x-free", Name = "x-free", IsFree = true };
        var paid = new OpenCodeZenModel { Id = "y", Name = "y", IsFree = false };
        var overrides = new Dictionary<string, ModelCapabilityOverride>
        {
            ["x-free"] = new() { Context = 1000, Output = 1000 },
            ["y"] = new() { Context = 2000, Output = 2000 }
        };

        OpenCodeZenModelCatalog.ApplyOverrides(free, overrides).IsFree.Should().BeTrue();
        OpenCodeZenModelCatalog.ApplyOverrides(paid, overrides).IsFree.Should().BeFalse();
    }
}
