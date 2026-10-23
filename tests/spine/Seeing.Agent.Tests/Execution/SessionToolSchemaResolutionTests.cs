using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// 结算视图的语义边界：SendsSchema 只回答“schema 是否下发”，不代表执行层可调用性。
/// </summary>
public class SessionToolSchemaResolutionTests
{
    private static ExposedTool Tool(string id, ToolExposureMode mode) =>
        new(
            id,
            Namespace: string.Empty,
            Signature: id,
            Schema: new FunctionToolSchema { Function = new FunctionSchema { Name = id } },
            EstimatedTokens: 10,
            Mode: mode);

    private static SessionToolSchemaResolution Resolution() =>
        new(
            [Tool("inline_tool", ToolExposureMode.Inline),
             Tool("deferred_tool", ToolExposureMode.Deferred)],
            Array.Empty<string>(),
            ToolExposureLayout.Tiered,
            CatalogText: "catalog",
            SchemaTokens: 10,
            CatalogTokens: 5);

    [Fact]
    public void SendsSchema_ShouldBeTrueOnlyForInline()
    {
        var resolution = Resolution();

        resolution.SendsSchema("inline_tool").Should().BeTrue();
        resolution.SendsSchema("deferred_tool").Should().BeFalse();
        resolution.SendsSchema("unknown_tool").Should().BeFalse();
    }

    [Fact]
    public void IsDeferred_ShouldBeTrueOnlyForDeferred()
    {
        var resolution = Resolution();

        resolution.IsDeferred("deferred_tool").Should().BeTrue();
        resolution.IsDeferred("inline_tool").Should().BeFalse();
    }
}
