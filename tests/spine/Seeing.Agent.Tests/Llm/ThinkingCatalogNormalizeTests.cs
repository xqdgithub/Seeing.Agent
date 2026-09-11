using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ThinkingCatalogNormalizeTests
{
    [Fact]
    public void ApplyThinkingCatalog_NoDefault_LeavesUnset()
    {
        var config = SupportedConfig(defaultKey: null, levels: ["high", "max"]);
        var request = new ChatRequest();

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().BeNull();
        request.ThinkingBudgetTokens.Should().BeNull();
    }

    [Fact]
    public void ApplyThinkingCatalog_UsesDefaultWhenUnset()
    {
        var config = SupportedConfig(defaultKey: "high", levels: ["high", "max"]);
        var request = new ChatRequest();

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().Be("high");
    }

    [Fact]
    public void ApplyThinkingCatalog_InvalidKey_WithoutDefault_Clears()
    {
        var config = SupportedConfig(defaultKey: null, levels: ["high"]);
        var request = new ChatRequest { ThinkingEffort = "nope" };

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().BeNull();
    }

    [Fact]
    public void ApplyThinkingCatalog_InvalidKey_WithDefault_FallsBack()
    {
        var config = SupportedConfig(defaultKey: "high", levels: ["high", "max"]);
        var request = new ChatRequest { ThinkingEffort = "nope" };

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().Be("high");
    }

    [Fact]
    public void ApplyThinkingCatalog_Unsupported_ClearsAll()
    {
        var config = new ModelConfig
        {
            Id = "m",
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions { Supported = false }
            }
        };
        var request = new ChatRequest
        {
            ThinkingEffort = "high",
            ThinkingBudgetTokens = 1000,
            EchoReasoningContent = true
        };

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().BeNull();
        request.ThinkingBudgetTokens.Should().BeNull();
        request.EchoReasoningContent.Should().BeFalse();
    }

    [Fact]
    public void ApplyThinkingCatalog_LevelBudget_Wins()
    {
        var config = new ModelConfig
        {
            Id = "m",
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions
                {
                    Supported = true,
                    BudgetTokens = 1000,
                    Levels =
                    [
                        new ThinkingLevel { Key = "high", BudgetTokens = 4096 }
                    ]
                }
            }
        };
        var request = new ChatRequest { ThinkingEffort = "high" };

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().Be("high");
        request.ThinkingBudgetTokens.Should().Be(4096);
    }

    [Fact]
    public void ApplyThinkingCatalog_OffKey_ClearsBudget()
    {
        var config = SupportedConfig(defaultKey: null, levels: ["disabled", "high"]);
        config.Options!.Thinking!.BudgetTokens = 8192;
        var request = new ChatRequest { ThinkingEffort = "disabled" };

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().Be("disabled");
        request.ThinkingBudgetTokens.Should().BeNull();
    }

    [Fact]
    public void ApplyThinkingCatalog_Interleaved_SetsEchoFlag()
    {
        var config = SupportedConfig(defaultKey: "high", levels: ["high"]);
        config.Options!.Thinking!.Interleaved = "reasoning_content";
        var request = new ChatRequest { ThinkingEffort = "high" };

        LlmService.ApplyThinkingCatalog(config, request);

        request.EchoReasoningContent.Should().BeTrue();
    }

    [Fact]
    public void ApplyThinkingCatalog_NoInterleaved_EchoFalse()
    {
        var config = SupportedConfig(defaultKey: "high", levels: ["high"]);
        var request = new ChatRequest { ThinkingEffort = "high" };

        LlmService.ApplyThinkingCatalog(config, request);

        request.EchoReasoningContent.Should().BeFalse();
    }

    private static ModelConfig SupportedConfig(string? defaultKey, string[] levels) => new()
    {
        Id = "m",
        Options = new ModelOptions
        {
            Thinking = new ThinkingOptions
            {
                Supported = true,
                Default = defaultKey,
                Levels = levels.Select(k => new ThinkingLevel { Key = k }).ToList()
            }
        }
    };
}
