using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.Agent.Tests.Scenarios;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 组12：SessionToolSchemaResolver 接入工具暴露裁剪——tiered 仅内联 pinned/已发现、
/// auto 阈值升级(粘滞不回退)、all 与改造前一致、顺序 pinned→discovered。
/// </summary>
public class SessionToolSchemaResolverExposureTests
{
    [Fact]
    public async Task Tiered_ShouldExposeOnlyPinnedAndDiscovered()
    {
        var session = SessionData.Create(scenario: "code");
        session.DiscoveredTools.Add("read");
        var services = await BuildAsync(session, new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            Exposure = ToolExposurePolicyMode.Tiered,
            PinnedTools = ["git_status"]
        });

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, new AgentDefinition
            {
                Name = "build",
                Runtime = AgentRuntime.Native,
                Exposure = ToolExposurePolicyMode.Tiered,
                PinnedTools = ["git_status"]
            }, TestContext.Current.CancellationToken);

        resolution.ToolSchemas.Select(s => s.Function!.Name)
            .Should().BeEquivalentTo(["git_status", "read"]);
        // scenario=code：memory_search 未启用不可用；可用集 {git_status, read, current_time}
        resolution.DeferredToolCount.Should().Be(1);   // current_time
        resolution.CatalogEntries.Select(e => e.Id)
            .Should().BeEquivalentTo(["current_time"]);
        resolution.InlineToolCount.Should().Be(2);
    }

    [Fact]
    public async Task Tiered_DiscoveredToolVanished_ShouldPruneNotThrow()
    {
        var session = SessionData.Create(scenario: "code");
        session.DiscoveredTools.Add("nope_tool");   // 已不在可用集
        var agent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            Exposure = ToolExposurePolicyMode.Tiered
        };
        var services = await BuildAsync(session, agent);

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        // 消失的 discovered 被裁剪，不内联、不抛
        resolution.ToolSchemas.Select(s => s.Function!.Name).Should().NotContain("nope_tool");
        session.DiscoveredTools.Should().NotContain("nope_tool");
    }

    [Fact]
    public async Task All_ShouldBehaveAsBefore_NoCatalog()
    {
        var session = SessionData.Create(scenario: "code");
        var agent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            Exposure = ToolExposurePolicyMode.All
        };
        var services = await BuildAsync(session, agent);

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.ToolSchemas.Select(s => s.Function!.Name)
            .Should().Contain(["git_status", "read", "current_time"]);
        resolution.CatalogEntries.Should().BeEmpty();
        resolution.DeferredToolCount.Should().Be(0);
    }

    [Fact]
    public async Task Auto_ShouldEscalate_WhenOverThreshold_AndStayEscalated()
    {
        var session = SessionData.Create(scenario: "code");
        var agent = new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native }; // 无覆盖 → 全局 auto
        // 全局阈值：EscalateToolCount=2，4 工具 → 升级
        var services = await BuildAsync(session, agent, escalateToolCount: 2);

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        session.ExposureEscalated.Should().BeTrue();
        resolution.DeferredToolCount.Should().BeGreaterThan(0);

        // 工具移除到阈值以下（Denied 排到只剩 1 个内联候选）后，仍粘滞分层，不自动降级
        var agent2 = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            DeniedTools = ["memory_search", "read", "current_time"]   // 仅剩 git_status
        };
        var resolution2 = await CreateResolver(services)
            .ResolveAsync(session.Id, agent2, TestContext.Current.CancellationToken);

        session.ExposureEscalated.Should().BeTrue();
        resolution2.ToolSchemas.Should().NotBeNull();
    }

    [Fact]
    public async Task Ordering_ShouldBePinnedThenDiscovered()
    {
        var session = SessionData.Create(scenario: "code");
        session.DiscoveredTools.Add("current_time");   // 发现序 1
        session.DiscoveredTools.Add("read");             // 发现序 2
        var agent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            Exposure = ToolExposurePolicyMode.Tiered,
            PinnedTools = ["git_status"]
        };
        var services = await BuildAsync(session, agent);

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.ToolSchemas.Select(s => s.Function!.Name)
            .Should().Equal("git_status", "current_time", "read");
    }

    private static SessionToolSchemaResolver CreateResolver(IServiceProvider services) =>
        new(services, services.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
            NullLogger<SessionToolSchemaResolver>.Instance);

    private static async Task<IServiceProvider> BuildAsync(
        SessionData session,
        AgentDefinition agent,
        int escalateToolCount = 9999)
    {
        var toolManager = new Seeing.Agent.Core.Tools.ToolManager(
            NullLogger<Seeing.Agent.Core.Tools.ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));
        foreach (var id in new[] { "git_status", "memory_search", "read", "current_time" })
            await toolManager.RegisterToolAsync(new NamedTool(id));

        var options = new SeeingAgentOptions
        {
            Tools = new ToolOptions
            {
                Exposure = new ToolExposureOptions
                {
                    PolicyMode = "auto",
                    EscalateToolCount = escalateToolCount,
                    EscalateSchemaTokens = 999999,
                    CatalogBudgetChars = 4000,
                    CatalogLineMaxChars = 200
                }
            }
        };
        var optionsMonitor =
            Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(
        [
            new ModuleDescriptor("git", ["git_status"], [], []),
            new ModuleDescriptor("memory", ["memory_search"], [], []),
            new ModuleDescriptor("filesystem", ["read"], [], []),
            new ModuleDescriptor("basic", ["current_time"], [], [])
        ]);
        catalog.ReplaceEnabled(["git", "memory", "filesystem", "basic"]);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(toolManager);
        services.AddSingleton<IToolManager>(toolManager);
        services.AddSingleton<IModuleCatalog>(catalog);
        services.AddSingleton<IScenarioCatalog>(new DelegatingScenarioCatalog(NarrowingScenarios.Resolve));
        services.AddSingleton(new ProcessSettlementOptions { HostDefaultScenario = "full" });
        services.AddSingleton<IPromptSectionContributor>(new FakeSectionContributor());
        services.AddSingleton<IOptionsMonitor<SeeingAgentOptions>>(optionsMonitor);
        services.AddSingleton<ITokenCounter, CharBasedTokenCounter>();
        // 暴露子系统接线
        services.AddSingleton<IToolExposurePolicy, AllToolExposurePolicy>();
        services.AddSingleton<IToolExposurePolicy, TieredToolExposurePolicy>();
        services.AddSingleton<ToolExposurePolicySelector>();
        services.AddSingleton<ToolExposureConfigResolver>();
        services.AddSingleton<IToolDiscoveryStore, SessionToolDiscoveryStore>();

        return services.BuildServiceProvider();
    }

    private sealed class NamedTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => "desc-" + Id;
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public System.Text.Json.JsonElement ParametersSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement arguments, ToolContext context) =>
            Task.FromResult(ToolResult.Succeeded("ok"));
    }

    private sealed class FakeSectionContributor : IPromptSectionContributor
    {
        public string SectionName => PromptSectionNames.Tools;
        public string Heading => "## Tools";
        public PromptSectionPlacement Placement => PromptSectionPlacement.AtEnd;
        public int Order => 0;
        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("tools");
    }
}
