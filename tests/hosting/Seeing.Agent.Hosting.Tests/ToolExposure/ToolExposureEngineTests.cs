using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// 暴露结算引擎：纯函数（分配模式、升级判定、发现裁剪、目录分组、度量）。
/// </summary>
public class ToolExposureEngineTests
{
    private static readonly ITokenCounter Counter = new CharBasedTokenCounter();

    private static FunctionToolSchema Schema(string id, string? ns = null, string desc = "desc")
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string" } },
            required = new[] { "path" }
        });
        return new FunctionToolSchema
        {
            Function = new FunctionSchema { Name = id, Description = desc, Parameters = parameters }
        };
    }

    private static EffectiveExposureConfig Config(
        ToolExposurePolicyMode mode,
        IReadOnlyList<string>? pinned = null,
        int escalateToolCount = 9999,
        int escalateSchemaTokens = 999999,
        int budget = 2000,
        int lineMax = 120) =>
        new(mode, pinned ?? Array.Empty<string>(), budget, lineMax, escalateToolCount, escalateSchemaTokens);

    private static ToolExposureInput Input(
        IReadOnlyList<FunctionToolSchema> tools,
        EffectiveExposureConfig config,
        IReadOnlyList<string>? discovered = null,
        bool escalated = false,
        IReadOnlySet<string>? available = null) =>
        new(tools, available, discovered ?? Array.Empty<string>(), escalated, config);

    [Fact]
    public void FlatLayout_ShouldInlineEverything_AndHideSearchTool()
    {
        var tools = new[] { Schema("git_status"), Schema("tool_search") };

        var result = ToolExposureEngine.Compute(Input(tools, Config(ToolExposurePolicyMode.All)), Counter);

        result.Layout.Should().Be(ToolExposureLayout.Flat);
        result.Tools.Single(t => t.Id == "git_status").Mode.Should().Be(ToolExposureMode.Inline);
        result.Tools.Should().NotContain(t => t.Id == "tool_search"); // 全量下检索工具隐藏
        result.CatalogText.Should().BeNull();
    }

    [Fact]
    public void TieredLayout_ShouldInlinePinnedDiscoveredAndSearchTool()
    {
        var tools = new[] { Schema("a"), Schema("b"), Schema("c"), Schema("tool_search") };

        var result = ToolExposureEngine.Compute(
            Input(tools, Config(ToolExposurePolicyMode.Tiered, pinned: ["a"]), discovered: ["b"]), Counter);

        result.Layout.Should().Be(ToolExposureLayout.Tiered);
        result.Tools.Where(t => t.Mode == ToolExposureMode.Inline).Select(t => t.Id)
            .Should().Equal("a", "b", "tool_search");
        result.Tools.Where(t => t.Mode == ToolExposureMode.Deferred).Select(t => t.Id)
            .Should().Equal("c");
        result.CatalogText.Should().NotBeNull();
        result.CatalogText!.Should().Contain("c(");
    }

    [Fact]
    public void AutoBelowThreshold_ShouldStayFlat()
    {
        var tools = new[] { Schema("a"), Schema("tool_search") };

        var result = ToolExposureEngine.Compute(
            Input(tools, Config(ToolExposurePolicyMode.Auto, escalateToolCount: 30)), Counter);

        result.Layout.Should().Be(ToolExposureLayout.Flat);
    }

    [Fact]
    public void AutoOverThreshold_ShouldEscalateToTiered()
    {
        var tools = new[] { Schema("a"), Schema("b"), Schema("tool_search") };

        var result = ToolExposureEngine.Compute(
            Input(tools, Config(ToolExposurePolicyMode.Auto, escalateToolCount: 2)), Counter);

        result.Layout.Should().Be(ToolExposureLayout.Tiered);
        result.Escalated.Should().BeTrue();
    }

    [Fact]
    public void EscalatedSession_ShouldStayTiered_EvenBelowThreshold()
    {
        var tools = new[] { Schema("a"), Schema("tool_search") };

        var result = ToolExposureEngine.Compute(
            Input(tools, Config(ToolExposurePolicyMode.Auto, escalateToolCount: 30), escalated: true), Counter);

        result.Layout.Should().Be(ToolExposureLayout.Tiered);
    }

    [Fact]
    public void TieredWithoutSearchTool_ShouldClampToFlat()
    {
        var tools = new[] { Schema("a"), Schema("b") };

        var result = ToolExposureEngine.Compute(Input(tools, Config(ToolExposurePolicyMode.Tiered)), Counter);

        result.Layout.Should().Be(ToolExposureLayout.Flat);
        result.Tools.Should().OnlyContain(t => t.Mode == ToolExposureMode.Inline);
    }

    [Fact]
    public void Discovered_ShouldBePrunedBySessionAvailableIds()
    {
        var tools = new[] { Schema("a"), Schema("tool_search") };
        var available = new HashSet<string>(new[] { "a", "tool_search" }, StringComparer.OrdinalIgnoreCase);

        var result = ToolExposureEngine.Compute(
            Input(tools, Config(ToolExposurePolicyMode.Tiered), discovered: ["gone", "a"], available: available),
            Counter);

        result.Tools.Should().NotContain(t => t.Id == "gone");
        result.Tools.Single(t => t.Id == "a").Mode.Should().Be(ToolExposureMode.Inline);
    }

    [Fact]
    public void Catalog_ShouldAlwaysListNamespaceWithCounts()
    {
        var tools = new[]
        {
            Schema("mcp__docs__search"), Schema("mcp__docs__read"), Schema("tool_search")
        };

        var result = ToolExposureEngine.Compute(Input(tools, Config(ToolExposurePolicyMode.Tiered)), Counter);

        result.CatalogText.Should().NotBeNull();
        result.CatalogText!.Should().Contain("### mcp__docs");
        result.CatalogText.Should().Contain("2 tools");
    }

    [Fact]
    public void Signature_ShouldBeSingleLineWithParameterSummary()
    {
        var tools = new[] { Schema("read", desc: "line one\n\nline two"), Schema("tool_search") };

        var result = ToolExposureEngine.Compute(Input(tools, Config(ToolExposurePolicyMode.Tiered)), Counter);

        var signature = result.Tools.Single(t => t.Id == "read").Signature;
        signature.Should().StartWith("read(path):");
        signature.Should().NotContain("\n");
    }

    [Fact]
    public void Compute_ShouldBeDeterministic()
    {
        var tools = new[] { Schema("b"), Schema("a"), Schema("tool_search") };
        var input = Input(tools, Config(ToolExposurePolicyMode.Tiered, pinned: ["b"]));

        var first = ToolExposureEngine.Compute(input, Counter);
        var second = ToolExposureEngine.Compute(input, Counter);

        first.Tools.Select(t => t.Id).Should().Equal(second.Tools.Select(t => t.Id));
        first.CatalogText.Should().Be(second.CatalogText);
        first.SchemaTokens.Should().Be(second.SchemaTokens);
    }
}
