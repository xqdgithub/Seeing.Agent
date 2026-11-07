using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    public void 落点_应为系统提示词_与技能清单同构()
    {
        // 目录是环境事实，不是"用户说了什么"：初始渲染进系统提示词（第 1 轮就在、位置固定），
        // 之后的变化由装配器统一走增量叙述消息（与 SkillsContextSource 相同形态）。
        new ToolCatalogContextSource(new FakeScope()).Target
            .Should().Be(ContextSourceTarget.System);
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
    public async Task 布局变平坦_应为不可用_不得叙述工具已移除()
    {
        // Tiered→Flat：工具此刻全部内联在 tools[] 里、完全可调。
        // 若按 Removed 处理会叙述「已不在目录中（不得再调用）」，写入与环境相反的持久事实。
        var scope = new FakeScope
        {
            ToolSettlement = BuildSettlement(ToolExposureLayout.Flat)
        };
        var source = new ToolCatalogContextSource(scope);

        var value = await source.ReadAsync();

        value.State.Should().Be(ContextValueState.Unavailable);
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
    public void 执行引擎注册_应把工具目录注册为上下文源()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // AddExecutionEngine 依赖外部已注册的会话/工作区服务，独立构建容器会失败；
        // 这里只断言 ServiceDescriptor（DI 连通性由 HostComposition 守门测试覆盖）。
        HostingServiceCollectionExtensions.AddExecutionEngine(services);

        services.Should().Contain(
            d => d.ServiceType == typeof(IContextSource)
                 && d.ImplementationType == typeof(ToolCatalogContextSource),
            "工具目录必须以上下文源注册，而不是系统提示词分节");
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
        params (string Namespace, string Id, string Description)[] tools) =>
        BuildSettlement(ToolExposureLayout.Tiered, tools);

    private static SessionToolSchemaResolution BuildSettlement(
        ToolExposureLayout layout,
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
            layout,
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
