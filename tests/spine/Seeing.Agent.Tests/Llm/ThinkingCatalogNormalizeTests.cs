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
    public void ApplyThinkingCatalog_NotSpecified_DoesNotFallBackToModelDefault()
    {
        // 模型默认档是「会话首次绑定模型」的初值（见 ModelManager.ApplyModelToSession），
        // 不再由请求期回落产生：否则会话里选「不指定」仍会发出模型默认档。
        var config = SupportedConfig(defaultKey: "high", levels: ["high", "max"]);
        var request = new ChatRequest();

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().BeNull();
        request.ThinkingBudgetTokens.Should().BeNull();
    }

    [Fact]
    public void ApplyThinkingCatalog_InvalidKey_Clears()
    {
        var config = SupportedConfig(defaultKey: "high", levels: ["high"]);
        var request = new ChatRequest { ThinkingEffort = "nope" };

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().BeNull("非法 key 不再静默回落模型默认档");
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

    /// <summary>
    /// 「模型默认关闭思考」的完整链路：模型声明默认档指向关闭档 →
    /// 会话首次绑定模型时把该默认档<b>播种</b>进会话 → 请求带上会话值 → 出站 disabled。
    /// 请求期不再回落默认档，所以这里显式传播种值，等价于会话里的真实状态。
    /// </summary>
    [Fact]
    public void ApplyThinkingCatalog_SeededDefaultOff_EmitsDisabledKey()
    {
        var config = SupportedConfig(defaultKey: "disabled", levels: ["disabled", "high"]);
        // 会话播种后的值就是模型的默认档。
        var request = new ChatRequest { ThinkingEffort = config.Options!.Thinking!.Default };

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().Be("disabled");
        request.ThinkingBudgetTokens.Should().BeNull();
    }

    /// <summary>
    /// 反例守门：<c>Supported=false</c> 表示「不声明思考能力」，会把 ThinkingEffort 清空，
    /// 出站走缺省分支——它<b>不是</b>「关闭思考」。要关闭必须留 Supported=true + 关闭档。
    /// </summary>
    [Fact]
    public void ApplyThinkingCatalog_NotSupported_IsNotOff()
    {
        var config = new ModelConfig
        {
            Id = "m",
            Options = new ModelOptions { Thinking = new ThinkingOptions { Supported = false } }
        };
        var request = new ChatRequest();

        LlmService.ApplyThinkingCatalog(config, request);

        request.ThinkingEffort.Should().BeNull("Supported=false 清空档位，出站会缺省开启，不能当关闭用");
    }

    /// <summary>
    /// 快速选择的候选清单必须与实际出站语义一致：
    /// <list type="bullet">
    /// <item>不含 <c>none</c>——它等同关闭档，混进强度档会让「关闭」出现两个 key；</item>
    /// <item>每个 key 都不能被 <see cref="ThinkingEffortKeys.IsOff"/> 判为关闭；
    /// <item>key 唯一且非空（否则选中后写进配置也是废值）。</item>
    /// </list>
    /// </summary>
    [Fact]
    public void WellKnownLevels_ShouldMatchOutboundSemantics()
    {
        var levels = ThinkingEffortKeys.WellKnownLevels;

        levels.Should().NotBeEmpty();
        levels.Should().OnlyHaveUniqueItems(level => level.Key);
        levels.Select(level => level.Key).Should().NotContain("none",
            "none 属于关闭档语义（IsOff 认它），强度候选里再列一次就会出现两个关闭 key");

        foreach (var level in levels)
        {
            level.Key.Should().NotBeNullOrWhiteSpace();
            ThinkingEffortKeys.IsOff(level.Key).Should().BeFalse(
                $"'{level.Key}' 是强度档，不能被当作关闭档");
            level.Label.Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>这些档位必须覆盖服务端 <c>reasoning_effort</c> 校验清单里除 none 外的全部取值。</summary>
    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    [InlineData("ultra")]
    public void WellKnownLevels_ShouldCoverServerAcceptedEfforts(string key)
        => ThinkingEffortKeys.WellKnownLevels.Select(level => level.Key).Should().Contain(key);

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
