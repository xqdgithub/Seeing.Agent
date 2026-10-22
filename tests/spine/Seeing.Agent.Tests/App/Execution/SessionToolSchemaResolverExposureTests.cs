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
/// 会话工具视图（唯一工具列表来源）：tiered 只内联 pinned/已发现/检索工具、
/// 视图只读（不写会话状态）、发现集按会话可用集裁剪、升级单调。
/// </summary>
public class SessionToolSchemaResolverExposureTests
{
    [Fact]
    public async Task Tiered_ShouldExposeOnlyPinnedAndDiscovered()
    {
        var (session, agent, services) = await SetupAsync(
            AgentDefinition(ToolExposurePolicyMode.Tiered, pinned: ["git_status"]),
            discovered: new[] { "read" });

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.Layout.Should().Be(ToolExposureLayout.Tiered);
        resolution.InlineSchemas.Select(s => s.Function!.Name)
            .Should().BeEquivalentTo(["git_status", "read", "tool_search"]);
        resolution.DeferredTools.Select(t => t.Id).Should().BeEquivalentTo(["current_time"]);
        resolution.CatalogText.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_ShouldNotWriteSessionState()
    {
        // 端口只读：结算不得修改会话的发现集、升级位与度量。
        var (session, agent, services) = await SetupAsync(
            AgentDefinition(ToolExposurePolicyMode.Tiered, pinned: ["git_status"]),
            discovered: new[] { "gone", "read" });

        await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        session.ToolExposure.GetDiscovered().Should().Equal("gone", "read");
        session.ToolExposure.Escalated.Should().BeFalse();
        session.ToolExposure.SchemaTokens.Should().BeNull();
        session.ToolExposure.CatalogTokens.Should().BeNull();
    }

    [Fact]
    public async Task Prune_ShouldUseSessionLevelSet_NotAgentFiltered()
    {
        // 发现集裁剪基准是会话级可用集：Agent 本轮禁用某工具不应把它从视图外剔除。
        var (session, agent, services) = await SetupAsync(
            AgentDefinition(ToolExposurePolicyMode.Tiered, denied: ["read"]),
            discovered: new[] { "read" });

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        // read 被 Agent 禁用 → 本轮不在候选中，但视图的会话可用集仍包含它
        resolution.SessionAvailableIds.Should().Contain("read");
    }

    [Fact]
    public async Task All_ShouldInlineEverything_NoCatalog()
    {
        var (session, agent, services) = await SetupAsync(AgentDefinition(ToolExposurePolicyMode.All));

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.Layout.Should().Be(ToolExposureLayout.Flat);
        resolution.InlineSchemas.Select(s => s.Function!.Name)
            .Should().Contain(["git_status", "read", "current_time"]);
        resolution.CatalogText.Should().BeNull();
        resolution.DeferredTools.Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_ShouldEscalateWhenOverThreshold()
    {
        var (session, agent, services) = await SetupAsync(
            new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native },
            escalateToolCount: 2);

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.Layout.Should().Be(ToolExposureLayout.Tiered);
    }

    [Fact]
    public async Task EscalatedSession_ShouldStayTiered()
    {
        var (session, agent, services) = await SetupAsync(
            new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native });
        session.ToolExposure.Commit(null, escalated: true, 0, 0);

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.Layout.Should().Be(ToolExposureLayout.Tiered);
    }

    [Fact]
    public async Task Tiered_WithoutSearchToolAvailable_ShouldClampToFlat()
    {
        var (session, agent, services) = await SetupAsync(
            AgentDefinition(ToolExposurePolicyMode.Tiered, denied: ["tool_search"]));

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.Layout.Should().Be(ToolExposureLayout.Flat);
        resolution.DeferredTools.Should().BeEmpty();
    }

    [Fact]
    public async Task Ordering_ShouldBePinnedThenDiscovered()
    {
        var (session, agent, services) = await SetupAsync(
            AgentDefinition(ToolExposurePolicyMode.Tiered, pinned: ["git_status"]),
            discovered: new[] { "current_time", "read" });

        var resolution = await CreateResolver(services)
            .ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.InlineSchemas.Select(s => s.Function!.Name)
            .Should().Equal("git_status", "current_time", "read", "tool_search");
    }

    [Fact]
    public async Task SchemaPrefix_ShouldBeStableAcrossRounds()
    {
        var (session, agent, services) = await SetupAsync(
            AgentDefinition(ToolExposurePolicyMode.Tiered, pinned: ["git_status"]),
            discovered: new[] { "read" });
        var resolver = CreateResolver(services);

        string[]? first = null;
        for (var i = 0; i < 20; i++)
        {
            var ids = (await resolver.ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken))
                .InlineSchemas.Select(s => s.Function!.Name).ToArray();
            if (first is null) first = ids;
            else ids.Should().Equal(first);
        }
    }

    [Fact]
    public async Task ShouldLogExposureMetrics()
    {
        var (session, agent, services) = await SetupAsync(AgentDefinition(ToolExposurePolicyMode.Tiered));
        var logger = new RecordingLogger<SessionToolSchemaResolver>();
        var resolver = new SessionToolSchemaResolver(
            services,
            services.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
            services.GetRequiredService<ToolExposureConfigResolver>(),
            services.GetRequiredService<ITokenCounter>(),
            logger);

        await resolver.ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        logger.DebugCount.Should().BeGreaterThan(0);
    }

    private static AgentDefinition AgentDefinition(
        ToolExposurePolicyMode mode,
        IReadOnlyList<string>? pinned = null,
        IReadOnlyList<string>? denied = null) => new()
    {
        Name = "build",
        Runtime = AgentRuntime.Native,
        Exposure = mode,
        PinnedTools = pinned?.ToList(),
        DeniedTools = denied?.ToList()
    };

    private static SessionToolSchemaResolver CreateResolver(IServiceProvider services) =>
        new(services,
            services.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
            services.GetRequiredService<ToolExposureConfigResolver>(),
            services.GetRequiredService<ITokenCounter>(),
            NullLogger<SessionToolSchemaResolver>.Instance);

    private static async Task<(SessionData Session, AgentDefinition Agent, IServiceProvider Services)> SetupAsync(
        AgentDefinition agent,
        IReadOnlyList<string>? discovered = null,
        int escalateToolCount = 9999)
    {
        var session = SessionData.Create(scenario: "code");
        if (discovered is not null)
        {
            foreach (var id in discovered)
                session.ToolExposure.AddPending([id]);
            session.ToolExposure.PromotePending();
        }

        var toolManager = new Seeing.Agent.Core.Tools.ToolManager(
            NullLogger<Seeing.Agent.Core.Tools.ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));
        foreach (var id in new[] { "git_status", "memory_search", "read", "current_time", "tool_search" })
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
        var optionsMonitor = Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == options);

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(
        [
            new ModuleDescriptor("git", ["git_status"], [], []),
            new ModuleDescriptor("memory", ["memory_search"], [], []),
            new ModuleDescriptor("filesystem", ["read"], [], []),
            new ModuleDescriptor("basic", ["current_time", "tool_search"], [], [])
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
        services.AddSingleton<ToolExposureConfigResolver>();

        return (session, agent, services.BuildServiceProvider());
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
}
