using System.Text.Json;
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Tools.ToolDiscovery.Tests;

public class ToolSearchToolTests
{
    private readonly Mock<ISessionToolSchemaResolver> _resolver = new();
    private readonly Mock<IToolDiscoveryStore> _store = new();

    private readonly ToolContext _context = new()
    {
        SessionId = "s1",
        Agent = new AgentDefinition { Name = "build" },
    };

    private ToolSearchTool CreateTool(params ExposedTool[] deferred)
    {
        // 视图 = 内联的 tool_search + 传入的 deferred；tool_search 只读这一个来源。
        var settlement = new SessionToolSchemaResolution(
            deferred, Array.Empty<string>(), ToolExposureLayout.Tiered, "catalog", 0, 0);
        _resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<string>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(settlement);

        _store
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
            .Returns(true);

        return new ToolSearchTool(_resolver.Object, _store.Object);
    }

    private static ExposedTool Entry(
        string id, string? ns = "fs", string signature = "sig()", string summary = "sum") =>
        new(id, ns ?? string.Empty, $"{signature} {summary}",
            new FunctionToolSchema
            {
                Function = new FunctionSchema
                {
                    Name = id,
                    Description = summary,
                    Parameters = JsonSerializer.SerializeToElement(new { type = "object" })
                }
            },
            10,
            ToolExposureMode.Deferred);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static int GetMetaInt(ToolResult result, string key) => Convert.ToInt32(result.Metadata[key]);

    [Fact]
    public async Task DetailName_ShouldReturnOnlyIds()
    {
        var tool = CreateTool(
            Entry("file_read", signature: "read(path)", summary: "读文件"),
            Entry("git_commit", ns: "git", signature: "commit(msg)", summary: "提交"));

        var result = await tool.ExecuteAsync(Args(new { detail = "name" }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("file_read").And.Contain("git_commit");
        result.Output.Should().NotContain("read(path)").And.NotContain("读文件").And.NotContain("提交");
    }

    [Fact]
    public async Task DetailSummary_ShouldIncludeSignatureAndSummary()
    {
        var tool = CreateTool(Entry("file_read", signature: "read(path)", summary: "读文件"));

        var result = await tool.ExecuteAsync(Args(new { }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("file_read").And.Contain("read(path)").And.Contain("读文件");
    }

    [Fact]
    public async Task DetailFull_ShouldIncludeDefinitionAndWriteDiscovery()
    {
        var tool = CreateTool(Entry("file_read"));

        var result = await tool.ExecuteAsync(Args(new { query = "file_read", detail = "full" }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("file_read");
        result.Output.Should().Contain("下一轮");
        _store.Verify(
            s => s.Add("s1", It.Is<IEnumerable<string>>(ids => ids.Contains("file_read"))),
            Times.Once);
    }

    [Fact]
    public async Task DetailSummary_ShouldNotWriteDiscovery()
    {
        var tool = CreateTool(Entry("file_read"));

        await tool.ExecuteAsync(Args(new { query = "file_read", detail = "summary" }), _context);

        _store.Verify(s => s.Add(It.IsAny<string>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task NamespaceFilter_ShouldLimitScope()
    {
        var tool = CreateTool(Entry("file_read", ns: "fs"), Entry("git_commit", ns: "git"));

        var result = await tool.ExecuteAsync(Args(new { @namespace = "fs" }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("file_read");
        result.Output.Should().NotContain("git_commit");
    }

    [Fact]
    public async Task EmptyQuery_ShouldListAllAsOverview()
    {
        var tool = CreateTool(
            Entry("alpha01"), Entry("beta02"), Entry("gamma03"));

        var result = await tool.ExecuteAsync(Args(new { }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("alpha01").And.Contain("beta02").And.Contain("gamma03");
        GetMetaInt(result, "total").Should().Be(3);
    }

    [Fact]
    public async Task NoMatch_ShouldReturnActionableGuidanceWithNamespaceCounts()
    {
        var tool = CreateTool(Entry("file_read", ns: "fs"), Entry("file_list", ns: "fs"), Entry("git_commit", ns: "git"));

        var result = await tool.ExecuteAsync(Args(new { query = "zzzqqqxyz" }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().NotBeEmpty();
        result.Output.Should().Contain("未命中");
        result.Output.Should().Contain("fs: 2 个").And.Contain("git: 1 个");
        result.Output.Should().Contain("建议");
    }

    [Fact]
    public async Task EmptyCorpus_ShouldStateAllToolsInlined()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(Args(new { query = "anything" }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().NotBeEmpty();
        result.Output.Should().Contain("已内联");
    }

    [Fact]
    public async Task ExactIdMatch_ShouldRankFirst()
    {
        var tool = CreateTool(Entry("file_reader"), Entry("file_read"));

        var result = await tool.ExecuteAsync(Args(new { query = "file_read", limit = 1 }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("1. file_read");
        result.Output.Should().NotContain("file_reader");
    }

    [Fact]
    public async Task FirstPage_ShouldReportTotalAndNext()
    {
        var tool = CreateTool(
            Entry("alpha01"), Entry("alpha02"), Entry("alpha03"), Entry("alpha04"),
            Entry("alpha05"), Entry("alpha06"), Entry("alpha07"), Entry("alpha08"),
            Entry("alpha09"), Entry("alpha10"), Entry("alpha11"), Entry("alpha12"));

        var result = await tool.ExecuteAsync(Args(new { limit = 2 }), _context);

        result.Success.Should().BeTrue();
        GetMetaInt(result, "total").Should().Be(12);
        GetMetaInt(result, "returned").Should().Be(2);
        GetMetaInt(result, "remaining").Should().Be(10);
        GetMetaInt(result, "next_offset").Should().Be(2);
    }

    [Fact]
    public async Task SecondPage_ShouldContinueFromOffset()
    {
        var tool = CreateTool(
            Entry("alpha01"), Entry("alpha02"), Entry("alpha03"), Entry("alpha04"),
            Entry("alpha05"), Entry("alpha06"), Entry("alpha07"), Entry("alpha08"),
            Entry("alpha09"), Entry("alpha10"), Entry("alpha11"), Entry("alpha12"));

        var result = await tool.ExecuteAsync(Args(new { limit = 2, offset = 2 }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("alpha03").And.Contain("alpha04");
        result.Output.Should().NotContain("alpha01");
        GetMetaInt(result, "next_offset").Should().Be(4);
    }

    [Fact]
    public async Task LastPage_ShouldHaveNullNext()
    {
        var tool = CreateTool(
            Entry("alpha01"), Entry("alpha02"), Entry("alpha03"), Entry("alpha04"),
            Entry("alpha05"), Entry("alpha06"), Entry("alpha07"), Entry("alpha08"),
            Entry("alpha09"), Entry("alpha10"), Entry("alpha11"), Entry("alpha12"));

        var result = await tool.ExecuteAsync(Args(new { limit = 2, offset = 10 }), _context);

        result.Success.Should().BeTrue();
        GetMetaInt(result, "remaining").Should().Be(0);
        result.Metadata.Should().NotContainKey("next_offset");
        result.Output.Should().Contain("已到末页");
    }

    [Fact]
    public async Task OffsetBeyondTotal_ShouldReturnEmptyWithRealTotal()
    {
        var tool = CreateTool(Entry("alpha01"), Entry("alpha02"));

        var result = await tool.ExecuteAsync(Args(new { offset = 99 }), _context);

        result.Success.Should().BeTrue();
        GetMetaInt(result, "total").Should().Be(2);
        GetMetaInt(result, "returned").Should().Be(0);
        result.Output.Should().Contain("offset 越界").And.Contain("[0, 2)");
    }

    [Fact]
    public async Task FullDetail_ShouldCapAtThreeRegardlessOfLimit()
    {
        var tool = CreateTool(
            Entry("alpha01"), Entry("alpha02"), Entry("alpha03"), Entry("alpha04"),
            Entry("alpha05"), Entry("alpha06"));

        var result = await tool.ExecuteAsync(Args(new { detail = "full", limit = 30 }), _context);

        result.Success.Should().BeTrue();
        _store.Verify(
            s => s.Add("s1", It.Is<IEnumerable<string>>(ids => ids.Count() == 3)),
            Times.Once);
        result.Output.Should().Contain("每页最多加载 3 条");
    }

    [Fact]
    public async Task Guidance_ShouldPreferNarrowingOverPaging()
    {
        var tool = CreateTool(
            Entry("alpha01"), Entry("alpha02"), Entry("alpha03"), Entry("alpha04"),
            Entry("alpha05"), Entry("alpha06"));

        var result = await tool.ExecuteAsync(Args(new { limit = 2 }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("更精确关键词").And.Contain("namespace");
        var narrowIndex = result.Output.IndexOf("更精确关键词", StringComparison.Ordinal);
        var offsetIndex = result.Output.IndexOf("offset=", StringComparison.Ordinal);
        narrowIndex.Should().BeGreaterThanOrEqualTo(0);
        offsetIndex.Should().BeGreaterThan(narrowIndex);
    }

    [Fact]
    public async Task FullDetail_ShouldStateNextTurnEffect()
    {
        var tool = CreateTool(Entry("file_read"));

        var result = await tool.ExecuteAsync(Args(new { query = "file_read", detail = "full" }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("下一轮");
    }

    [Fact]
    public async Task ShouldNotHaveDedicatedQuota()
    {
        var tool = CreateTool(Entry("alpha01"), Entry("alpha02"), Entry("alpha03"));

        for (var i = 0; i < 5; i++)
        {
            var result = await tool.ExecuteAsync(Args(new { query = "alpha" }), _context);
            result.Success.Should().BeTrue();
            result.Error.Should().BeNull();
        }

        var last = await tool.ExecuteAsync(Args(new { query = "alpha", detail = "full" }), _context);
        last.Success.Should().BeTrue();
        last.Metadata.Keys.Should().NotContain(k =>
            k.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            k.Contains("剩余次数", StringComparison.Ordinal) ||
            k.Contains("searches", StringComparison.OrdinalIgnoreCase));
    }
}
