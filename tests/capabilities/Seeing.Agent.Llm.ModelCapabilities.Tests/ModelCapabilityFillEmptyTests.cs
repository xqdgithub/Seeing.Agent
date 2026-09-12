using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Llm.ModelCapabilities.Tests;

public class ModelCapabilityFillEmptyTests
{
    [Fact]
    public void Apply_空字段应被补全()
    {
        var target = new ModelConfig
        {
            Id = "m1",
            Provider = "p1",
            Limit = new ModelLimits { Context = 4096, Output = 4096 }
        };
        var source = new ModelCapabilityEntry
        {
            ModelId = "m1",
            Name = "Model One",
            Limit = new ModelCapabilityLimits { Context = 128000, Output = 8192 },
            Modalities = new ModelModalities { Input = ["text"], Output = ["text"] },
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions
                {
                    Supported = true,
                    Interleaved = "reasoning_content",
                    Levels =
                    [
                        new ThinkingLevel { Key = "disabled" },
                        new ThinkingLevel { Key = "high" }
                    ]
                }
            },
            Pricing = new ModelPricing { Input = 0.1, Output = 0.2 }
        };

        ModelCapabilityFillEmpty.Apply(target, source);

        target.Name.Should().Be("Model One");
        target.Limit.Context.Should().Be(128000);
        target.Limit.Output.Should().Be(8192);
        target.Modalities.Input.Should().ContainSingle("text");
        target.Options!.Thinking!.Supported.Should().BeTrue();
        target.Options.Thinking.Levels.Should().HaveCount(2);
        target.Pricing!.Input.Should().Be(0.1);
    }

    [Fact]
    public void Apply_非默认Limit不应被覆盖()
    {
        var target = new ModelConfig
        {
            Id = "m1",
            Limit = new ModelLimits { Context = 32000, Output = 4096 }
        };
        var source = new ModelCapabilityEntry
        {
            ModelId = "m1",
            Limit = new ModelCapabilityLimits { Context = 128000, Output = 8192 }
        };

        ModelCapabilityFillEmpty.Apply(target, source);

        target.Limit.Context.Should().Be(32000);
        target.Limit.Output.Should().Be(8192);
    }

    [Fact]
    public void Apply_已有Thinking不覆盖()
    {
        var target = new ModelConfig
        {
            Id = "m1",
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions
                {
                    Supported = true,
                    Levels = [new ThinkingLevel { Key = "low", Label = "低" }]
                }
            }
        };
        var source = new ModelCapabilityEntry
        {
            ModelId = "m1",
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions
                {
                    Supported = true,
                    Levels =
                    [
                        new ThinkingLevel { Key = "disabled" },
                        new ThinkingLevel { Key = "max" }
                    ]
                }
            }
        };

        ModelCapabilityFillEmpty.Apply(target, source);

        target.Options!.Thinking!.Levels.Should().ContainSingle(l => l.Key == "low");
    }

    [Fact]
    public void Apply_显式4096视为未设置可被补全()
    {
        // default means unset for FillEmpty
        var target = new ModelConfig
        {
            Id = "m1",
            Limit = new ModelLimits { Context = 4096, Output = 4096 }
        };
        var source = new ModelCapabilityEntry
        {
            ModelId = "m1",
            Limit = new ModelCapabilityLimits { Context = 200000, Output = 32000 }
        };

        ModelCapabilityFillEmpty.Apply(target, source);

        target.Limit.Context.Should().Be(200000);
        target.Limit.Output.Should().Be(32000);
    }
}
