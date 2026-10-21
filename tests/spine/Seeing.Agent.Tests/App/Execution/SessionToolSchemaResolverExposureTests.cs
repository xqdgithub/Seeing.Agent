using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        resolution.DeferredEntries.Select(e => e.Id)
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
        resolution.DeferredEntries.Should().BeEmpty();
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
            services.GetRequiredService<ToolExposureConfigResolver>(),
            services.GetRequiredService<ToolExposurePolicySelector>(),
            services.GetRequiredService<ITokenCounter>(),
            NullLogger<SessionToolSchemaResolver>.Instance);

    [Fact]
    public async Task SchemaPrefix_ShouldBeStableAcrossRounds()
    {
        // 17.4：同一会话+Agent 连续多轮，inline 工具 id 序列完全一致（保护 provider 前缀缓存）
        var session = SessionData.Create(scenario: "code");
        var agent = new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native, Exposure = ToolExposurePolicyMode.Tiered, PinnedTools = ["git_status"] };
        session.DiscoveredTools.Add("read");
        var services = await BuildAsync(session, agent);
        var resolver = CreateResolver(services);

        string[]? first = null;
        for (var i = 0; i < 20; i++)
        {
            var r = await resolver.ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);
            var ids = r.ToolSchemas.Select(s => s.Function!.Name).ToArray();
            if (first is null) first = ids;
            else ids.Should().Equal(first);
        }
    }

    [Fact]
    public async Task Escalation_ShouldFlipOnceAndNeverDowngrade()
    {
        // 17.5：auto 越阈值升级一次并粘滞；后续即使工具减少也不回退
        var session = SessionData.Create(scenario: "code");
        var agent = new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native };
        var services = await BuildAsync(session, agent, escalateToolCount: 2);
        var resolver = CreateResolver(services);

        await resolver.ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);
        session.ExposureEscalated.Should().BeTrue();

        // 工具骤减（denied 剩 1）仍不回退
        var few = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            DeniedTools = ["memory_search", "read", "current_time"]
        };
        await resolver.ResolveAsync(session.Id, few, TestContext.Current.CancellationToken);
        session.ExposureEscalated.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldLogExposureMetrics_WhenTieredActive()
    {
        var session = SessionData.Create(scenario: "code");
        var agent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            Exposure = ToolExposurePolicyMode.Tiered
        };
        var services = await BuildAsync(session, agent);
        var logger = new RecordingLogger<SessionToolSchemaResolver>();
        var resolver = new SessionToolSchemaResolver(
            services, services.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
            services.GetRequiredService<ToolExposureConfigResolver>(),
            services.GetRequiredService<ToolExposurePolicySelector>(),
            services.GetRequiredService<ITokenCounter>(), logger);

        await resolver.ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        logger.DebugCount.Should().BeGreaterThan(0);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public int DebugCount;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug) DebugCount++;
        }
    }

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
