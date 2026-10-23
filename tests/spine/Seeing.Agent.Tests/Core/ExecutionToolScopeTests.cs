using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Execution;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// 执行级工具可见性作用域：可调用集 = 内联 ∪ 本执行已加载；
/// 只有本轮结算为 deferred 的工具才可被加载（防止绕过 Agent 过滤）。
/// </summary>
public class ExecutionToolScopeTests
{
    private static SessionToolSchemaResolution Settlement(
        params (string Id, ToolExposureMode Mode)[] tools) =>
        new(
            tools.Select(t => new ExposedTool(
                t.Id,
                Namespace: string.Empty,
                Signature: t.Id,
                Schema: new FunctionToolSchema
                {
                    Function = new FunctionSchema { Name = t.Id }
                },
                EstimatedTokens: 10,
                Mode: t.Mode)).ToList(),
            Array.Empty<string>(),
            ToolExposureLayout.Tiered,
            CatalogText: null,
            SchemaTokens: 0,
            CatalogTokens: 0);

    [Fact]
    public void InlineTool_ShouldBeCallable()
    {
        var scope = new ExecutionToolScope(Settlement(("read", ToolExposureMode.Inline)));

        scope.IsCallable("read").Should().BeTrue();
    }

    [Fact]
    public void DeferredTool_ShouldNotBeCallable_UntilLoaded()
    {
        var scope = new ExecutionToolScope(Settlement(("bash", ToolExposureMode.Deferred)));

        scope.IsCallable("bash").Should().BeFalse();
        scope.TryLoad(["bash"]).Should().Be(1);
        scope.IsCallable("bash").Should().BeTrue();
    }

    [Fact]
    public void TryLoad_ShouldRejectToolOutsideSettlement()
    {
        // Agent 白/黑名单过滤掉的工具不在结算视图内：即使被登记也不得放行。
        var scope = new ExecutionToolScope(Settlement(("read", ToolExposureMode.Inline)));

        scope.TryLoad(["denied_tool"]).Should().Be(0);
        scope.IsCallable("denied_tool").Should().BeFalse();
    }

    [Fact]
    public void TryLoad_ShouldRejectInlineTool_AndBeIdempotent()
    {
        var scope = new ExecutionToolScope(Settlement(
            ("read", ToolExposureMode.Inline),
            ("bash", ToolExposureMode.Deferred)));

        scope.TryLoad(["read"]).Should().Be(0);

        scope.TryLoad(["bash", "BASH"]).Should().Be(1);
        scope.TryLoad(["bash"]).Should().Be(0);
        scope.LoadedIds.Should().Equal("bash");
    }

    [Fact]
    public void LoadedIds_ShouldPreserveLoadOrder()
    {
        var scope = new ExecutionToolScope(Settlement(
            ("a", ToolExposureMode.Deferred),
            ("b", ToolExposureMode.Deferred)));

        scope.TryLoad(["b", "a"]);

        scope.LoadedIds.Should().Equal("b", "a");
    }

    [Fact]
    public void NullSettlement_ShouldMakeNothingCallable()
    {
        var scope = new ExecutionToolScope(null);

        scope.IsCallable("read").Should().BeFalse();
        scope.IsDeferred("read").Should().BeFalse();
        scope.TryLoad(["read"]).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentLoadAndCheck_ShouldBeConsistent()
    {
        var ids = Enumerable.Range(0, 200).Select(i => $"tool_{i}").ToArray();
        var scope = new ExecutionToolScope(
            Settlement(ids.Select(id => (id, ToolExposureMode.Deferred)).ToArray()));

        await Task.WhenAll(ids.Select(id => Task.Run(() =>
        {
            scope.TryLoad([id, id.ToUpperInvariant()]);
            scope.IsCallable(id).Should().BeTrue();
        })));

        scope.LoadedIds.Should().HaveCount(ids.Length).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void DeferredTools_ShouldExposeSettlementDeferredOnly()
    {
        var scope = new ExecutionToolScope(Settlement(
            ("read", ToolExposureMode.Inline),
            ("bash", ToolExposureMode.Deferred)));

        scope.DeferredTools.Select(t => t.Id).Should().Equal("bash");
    }

    [Fact]
    public void IsDeferred_ShouldReflectSettlementMode()
    {
        var scope = new ExecutionToolScope(Settlement(
            ("read", ToolExposureMode.Inline),
            ("bash", ToolExposureMode.Deferred)));

        scope.IsDeferred("bash").Should().BeTrue();
        scope.IsDeferred("read").Should().BeFalse();
    }
}
