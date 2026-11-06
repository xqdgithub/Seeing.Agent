using FluentAssertions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ToolCatalogContextSourceTests
{
    [Fact]
    public async Task 结算缺失_应为不可用_而非已删除()
    {
        var source = new ToolCatalogContextSource(new FakeScope());

        var value = await source.ReadAsync();

        value.State.Should().Be(ContextValueState.Unavailable,
            "结算缺失属宿主接线问题，不得叙述为目录已删除");
    }

    [Fact]
    public void 落点_应为会话消息_目录不得进系统提示词()
    {
        new ToolCatalogContextSource(new FakeScope()).Target
            .Should().Be(ContextSourceTarget.Message);
    }

    [Fact]
    public async Task canonical_应只含命名空间与id_不含描述()
    {
        var scope = new FakeScope
        {
            ToolSettlement = BuildSettlement(
                ("mcp__docs", "mcp__docs__x", "很长的描述文本"))
        };
        var source = new ToolCatalogContextSource(scope);

        var value = await source.ReadAsync();

        var json = value.Canonical!.Value.GetRawText();
        json.Should().Contain("mcp__docs");
        json.Should().Contain("mcp__docs__x");
        json.Should().NotContain("很长的描述文本",
            "描述可能被 tool.definition Hook 改写，放进 canonical 会造成无关叙述");
    }

    [Fact]
    public async Task 无延迟工具_应为已删除()
    {
        var scope = new FakeScope { ToolSettlement = BuildSettlement() };
        var source = new ToolCatalogContextSource(scope);

        var value = await source.ReadAsync();

        value.State.Should().Be(ContextValueState.Removed);
    }

    [Fact]
    public async Task 初始渲染_应返回目录正文()
    {
        var scope = new FakeScope
        {
            ToolSettlement = BuildSettlement(("通用", "read", "读取文件"))
        };
        var source = new ToolCatalogContextSource(scope);

        var value = await source.ReadAsync();

        value.State.Should().Be(ContextValueState.Value);
        source.RenderInitial(value).Should().Be("CATALOG-BODY");
    }

    [Fact]
    public async Task 增量渲染_无变化时应返回null()
    {
        var scope = new FakeScope
        {
            ToolSettlement = BuildSettlement(("通用", "read", "读取文件"))
        };
        var source = new ToolCatalogContextSource(scope);
        var value = await source.ReadAsync();

        source.Render(value, value).Should().BeNull();
    }

    // ===== 脚手架 =====

    private static SessionToolSchemaResolution BuildSettlement(
        params (string Namespace, string Id, string Description)[] tools)
    {
        var exposed = tools
            .Select(t => new ExposedTool(
                t.Id,
                t.Namespace,
                Signature: $"{t.Id}()",
                Schema: new FunctionToolSchema
                {
                    Function = new FunctionSchema { Name = t.Id, Description = t.Description }
                },
                EstimatedTokens: 10,
                Mode: ToolExposureMode.Deferred))
            .ToList();

        return new SessionToolSchemaResolution(
            exposed,
            Array.Empty<string>(),
            ToolExposureLayout.Tiered,
            CatalogText: exposed.Count == 0 ? null : "CATALOG-BODY",
            SchemaTokens: 0,
            CatalogTokens: 10);
    }

    private sealed class FakeScope : IContextExecutionScope
    {
        public string SessionId { get; set; } = "s1";
        public string Cwd { get; set; } = ".";
        public string WorkspaceRoot { get; set; } = ".";
        public SkillCatalogSnapshot? SkillSnapshot { get; set; }
        public string? UserQuery { get; set; }
        public SessionToolSchemaResolution? ToolSettlement { get; set; }
    }
}
