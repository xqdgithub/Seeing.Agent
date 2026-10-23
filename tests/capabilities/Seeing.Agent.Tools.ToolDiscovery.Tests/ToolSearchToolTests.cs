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
    private readonly FakeScope _scope = new();

    private readonly ToolContext _context = new()
    {
        SessionId = "s1",
        Agent = new AgentDefinition { Name = "build" },
    };

    public ToolSearchToolTests()
    {
        _context.ToolScope = _scope;
    }

    private ToolSearchTool CreateTool(params ExposedTool[] deferred)
    {
        // 视图 = 内联的 tool_search + 传入的 deferred；tool_search 只读这一个来源。
        var settlement = new SessionToolSchemaResolution(
            deferred, Array.Empty<string>(), ToolExposureLayout.Tiered, "catalog", 0, 0);
        _resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<string>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(settlement);

        // 执行器会把同一份结算冻结进作用域；测试夹具同步注入，保证语料与真实链路一致。
        _scope.Deferred = deferred;

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
        result.Output.Should().Contain("本次执行内可直接调用");
        result.LoadedToolIds.Should().Equal("file_read");
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
    public void ParameterDescription_ShouldUseSameExecutionTerminology()
    {
        // 参数描述是模型实际读到的 schema，不能再说“下一轮生效”。
        var tool = CreateTool(Entry("file_read"));
        var detailDescription = tool.ParametersSchema
            .GetProperty("properties")
            .GetProperty("detail")
            .GetProperty("description")
            .GetString();

        detailDescription.Should().NotContain("下一轮");
        detailDescription.Should().Contain("本次执行内");
        tool.Description.Should().NotContain("下一轮");
    }

    [Fact]
    public async Task FullDetail_ShouldStateSameExecutionCallability()
    {
        var tool = CreateTool(Entry("file_read"));

        var result = await tool.ExecuteAsync(Args(new { query = "file_read", detail = "full" }), _context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("本次执行内可直接调用");
        result.LoadedToolIds.Should().Equal("file_read");
        // 加载由执行器统一应用，能力包不得直接写作用域
        _scope.LoadedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DetailFull_WhenDiscoveryStoreFails_ShouldNotDeclareLoaded()
    {
        var tool = CreateTool(Entry("file_read"));
        _store
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
            .Returns(false);

        var result = await tool.ExecuteAsync(Args(new { query = "file_read", detail = "full" }), _context);

        result.LoadedToolIds.Should().BeNull();
        result.Output.Should().Contain("登记失败");
    }

    [Fact]
    public async Task Corpus_ShouldPreferExecutionScopeSnapshot()
    {
        // 执行作用域存在时，检索语料必须取本轮冻结快照，而不是现场重新结算。
        var scope = new FakeScopeWithDeferred(Entry("from_scope"));
        var context = new ToolContext
        {
            SessionId = "s1",
            Agent = new AgentDefinition { Name = "build" },
            ToolScope = scope
        };
        var tool = CreateTool(Entry("from_resolver"));

        var result = await tool.ExecuteAsync(Args(new { }), context);

        result.Output.Should().Contain("from_scope").And.NotContain("from_resolver");
    }

    [Fact]
    public async Task DetailFull_WithoutExecutionScope_ShouldPromiseOnlyLaterExecutions()
    {
        // 非执行器路径（未透传作用域）不得承诺"本次执行可用"，否则就是假消息。
        var tool = CreateTool(Entry("file_read"));
        var context = new ToolContext
        {
            SessionId = "s1",
            Agent = new AgentDefinition { Name = "build" }
        };

        var result = await tool.ExecuteAsync(Args(new { query = "file_read", detail = "full" }), context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("下一次执行起");
        result.Output.Should().NotContain("本次执行内可直接调用");
        result.LoadedToolIds.Should().Equal("file_read");
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

    /// <summary>执行级作用域测试替身：记录加载顺序，可调性只看加载集。</summary>
    private sealed class FakeScope : IExecutionToolScope
    {
        private readonly List<string> _loaded = new();

        public ExposedTool[] Deferred { get; set; } = [];

        public IReadOnlyList<string> LoadedIds => _loaded;

        public IReadOnlyList<ExposedTool> DeferredTools => Deferred;

        public bool IsCallable(string toolId) =>
            _loaded.Contains(toolId, StringComparer.OrdinalIgnoreCase);

        public bool IsDeferred(string toolId) => true;

        public int TryLoad(IEnumerable<string> toolIds)
        {
            var added = 0;
            foreach (var id in toolIds)
            {
                if (!_loaded.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    _loaded.Add(id);
                    added++;
                }
            }

            return added;
        }
    }

    /// <summary>带冻结语料的执行级作用域替身。</summary>
    private sealed class FakeScopeWithDeferred(params ExposedTool[] deferred) : IExecutionToolScope
    {
        public IReadOnlyList<string> LoadedIds => [];

        public IReadOnlyList<ExposedTool> DeferredTools => deferred;

        public bool IsCallable(string toolId) => false;

        public bool IsDeferred(string toolId) => true;

        public int TryLoad(IEnumerable<string> toolIds) => 0;
    }
}
