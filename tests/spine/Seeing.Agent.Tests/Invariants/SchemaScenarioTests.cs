using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Core.Modules;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// P9-T14：schema 唯一计算 + 会话场景不变量（§8）。
/// </summary>
public class SchemaScenarioTests
{
    [Fact]
    public void AgentExecutor_MustNotSelfComputeToolSchemas()
    {
        typeof(AgentExecutor)
            .GetMethod("GetToolSchemas", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Should().BeNull("schema 唯一计算点在 ExecutionJobService；executor 只读 context.ToolSchemas");
    }

    [Fact]
    public async Task SchemaSnapshotEvent_MustMatchAgentContextToolSchemas_SameSourceAsChatRequestTools()
    {
        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        AgentContext? captured = null;
        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext ctx, CancellationToken _) =>
            {
                captured = ctx;
                return EmptyStream();
            });

        var session = SessionData.Create(scenario: "code");
        using var fixture = await CreateFixtureAsync(session, publisher.Object, executor.Object);

        var result = await fixture.Service.SubmitAsync(
            session.Id,
            ChatInput.FromText("hi"),
            new ChatOptions
            {
                AgentId = "general",
                SkipUserMessagePersist = true,
                SkipInstructionInject = true
            }, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent c && c.ExecutionId == result.ExecutionId));

        var snapshot = published.Select(p => p.Event).OfType<SchemaSnapshotEvent>().Single();
        captured.Should().NotBeNull();

        var contextToolIds = captured!.ToolSchemas!
            .Where(s => s.Function != null)
            .Select(s => s.Function.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // SchemaSnapshotEvent.ToolIds 与 AgentContext.ToolSchemas（→ ChatRequest.Tools）同源
        snapshot.ToolIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Should().Equal(contextToolIds);
        snapshot.ToolIds.Should().Contain("git_status");
        snapshot.ToolIds.Should().NotContain("memory_search");
    }

    [Fact]
    public async Task ModelVisibleSchema_SameModuleSet_ReplayRebuildsIdenticalToolIds()
    {
        var catalog = CreateCatalog(
            processEnabled: ["git", "memory", "filesystem", "basic"],
            modules:
            [
                Desc("git", ["git_status", "git_diff"]),
                Desc("memory", ["memory_search"]),
                Desc("filesystem", ["read"]),
                Desc("basic", ["current_time"])
            ]);

        var session = SessionData.Create(scenario: "code");
        var settle = SessionSettlement.Compute(session, catalog, processScenario: "full", userToolsDisabled: null);

        var toolManager = CreateToolManager(
            "git_status", "git_diff", "memory_search", "read", "current_time");
        var agent = new AgentDefinition { Name = "general" };

        var original = await toolManager.GetToolSchemasAsync(settle.SettledToolIds, agent, TestContext.Current.CancellationToken);
        var snapshotToolIds = original
            .Where(s => s.Function != null)
            .Select(s => s.Function.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 同模块集重放：用快照 toolIds 再取 schema → 可完整重建
        var rebuilt = await toolManager.GetToolSchemasAsync(snapshotToolIds, agent, TestContext.Current.CancellationToken);
        rebuilt.Select(s => s.Function!.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Should().Equal(snapshotToolIds);
    }

    [Fact]
    public async Task ModelVisibleSchema_CrossProcessModuleShrink_IntersectsAndWarns_DoesNotRefuse()
    {
        // 跨进程：available 收缩（memory 包未引用）→ Boot 能力集仍可结算，取交集并告警，不拒启
        var engine = new SettlementEngine(new ModuleCatalog(), NullLogger<SettlementEngine>.Instance);
        var result = await engine.SettleAsync(new SettlementInput
        {
            Available =
            [
                Desc("io.local"),
                Desc("basic"),
                Desc("git", dependsOn: ["io.local"]),
            ],
            ConfiguredBoot = "code",
            ResolveCapabilitySet = _ => new CapabilitySetDefinition(
                "code",
                BuiltInCapabilitySets.Code,
                Array.Empty<string>()),
        }, TestContext.Current.CancellationToken);

        result.Enabled.Should().Contain(["basic", "git", "io.local"]);
        result.Enabled.Should().NotContain("filesystem"); // 未在 available
        result.Warnings.Should().NotBeEmpty();
        result.Warnings.Should().Contain(w =>
            w.Contains("filesystem", StringComparison.OrdinalIgnoreCase) ||
            w.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
            w.Contains("subagent", StringComparison.OrdinalIgnoreCase));

        // 快照重放：旧进程可见 toolIds ∩ 当前 available tools → 告警缺失、不抛
        var snapshotFromOldProcess = new[] { "git_status", "read", "memory_search" };
        var currentAvailableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "git_status", "current_time"
        };

        var (rebuilt, missing) = ReplayIntersect(snapshotFromOldProcess, currentAvailableTools);
        rebuilt.Should().Equal("git_status");
        missing.Should().BeEquivalentTo(["read", "memory_search"]);
    }

    [Fact]
    public void SessionScenarios_CodeAndWork_ParallelNarrowDistinctTools()
    {
        var catalog = CreateCatalog(
            processEnabled: ["git", "memory", "filesystem", "basic"],
            modules:
            [
                Desc("git", ["git_status"]),
                Desc("memory", ["memory_search"]),
                Desc("filesystem", ["read"]),
                Desc("basic", ["current_time"])
            ]);

        var settleA = SessionSettlement.Compute(
            SessionData.Create(scenario: "code"), catalog, processScenario: "full", userToolsDisabled: null);
        var settleB = SessionSettlement.Compute(
            SessionData.Create(scenario: "work"), catalog, processScenario: "full", userToolsDisabled: null);

        settleA.SettledToolIds.Should().Contain("git_status");
        settleA.SettledToolIds.Should().NotContain("memory_search");
        settleB.SettledToolIds.Should().Contain("memory_search");
        settleB.SettledToolIds.Should().NotContain("git_status");
    }

    [Fact]
    public async Task SwitchingSessionScenario_NextSubmitChangesSchema_ProcessActivateUnchanged()
    {
        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var session = SessionData.Create(scenario: "code");
        var git = new TrackingModule("git", ["git_status"]);
        var memory = new TrackingModule("memory", ["memory_search"]);
        var filesystem = new TrackingModule("filesystem", ["read"]);
        var basic = new TrackingModule("basic", ["current_time"]);

        using var fixture = await CreateFixtureAsync(
            session,
            publisher.Object,
            executor.Object,
            modules: [git, memory, filesystem, basic]);

        // 进程级 Activate 快照（会话切场景不得改动）
        var activatedBefore = fixture.Lifecycle!.Activated.OrderBy(x => x).ToArray();
        activatedBefore.Should().BeEquivalentTo(["basic", "filesystem", "git", "memory"]);

        var r1 = await fixture.Service.SubmitAsync(
            session.Id, ChatInput.FromText("1"),
            new ChatOptions { AgentId = "general", SkipUserMessagePersist = true, SkipInstructionInject = true }, TestContext.Current.CancellationToken);
        r1.Success.Should().BeTrue();
        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent c && c.ExecutionId == r1.ExecutionId));

        var snap1 = published.Select(p => p.Event).OfType<SchemaSnapshotEvent>()
            .Last(s => s.ExecutionId == r1.ExecutionId);
        snap1.ToolIds.Should().Contain("git_status");
        snap1.ToolIds.Should().NotContain("memory_search");

        session.Scenario = "work";

        var r2 = await fixture.Service.SubmitAsync(
            session.Id, ChatInput.FromText("2"),
            new ChatOptions { AgentId = "general", SkipUserMessagePersist = true, SkipInstructionInject = true }, TestContext.Current.CancellationToken);
        r2.Success.Should().BeTrue();
        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent c && c.ExecutionId == r2.ExecutionId));

        var snap2 = published.Select(p => p.Event).OfType<SchemaSnapshotEvent>()
            .Last(s => s.ExecutionId == r2.ExecutionId);
        snap2.ToolIds.Should().Contain("memory_search");
        snap2.ToolIds.Should().NotContain("git_status");

        fixture.Lifecycle.Activated.OrderBy(x => x).Should().Equal(activatedBefore);
        git.ActivateCount.Should().Be(1);
        memory.ActivateCount.Should().Be(1);
        git.DeactivateCount.Should().Be(0);
        memory.DeactivateCount.Should().Be(0);
    }

    [Fact]
    public async Task InFlightBoundary_ProcessReloadDefersDeactivate_SessionSwitchOnlyAffectsNextSubmit()
    {
        // —— 进程级：在途时 Deactivate 推迟 ——
        var a = new TrackingModule("a");
        var b = new TrackingModule("b");
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(SettlementEngine.ToDescriptors([a, b]));
        catalog.ReplaceEnabled(["a", "b"]);
        var lifecycle = new ModuleLifecycleManager(catalog, [a, b], new ServiceCollection().BuildServiceProvider(), NullLogger<ModuleLifecycleManager>.Instance);
        await lifecycle.ActivateAsync(TestContext.Current.CancellationToken);

        var options = new MutableOptions(new SeeingAgentOptions
        {
            Boot = "only-a",
            CapabilitySets = new Dictionary<string, CapabilitySetConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["only-a"] = new() { Modules = ["a"] },
            },
        });
        var stillInFlight = true;
        var inFlight = new Mock<IExecutionInFlightBoundary>();
        inFlight.Setup(x => x.HasInFlight()).Returns(() => stillInFlight);
        inFlight.Setup(x => x.ListInFlightExecutionIds()).Returns([]);
        inFlight.Setup(x => x.CancelAllInFlightAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var handler = new ModuleSettlementReloadHandler(
            new SettlementEngine(catalog),
            lifecycle,
            catalog,
            options,
            [a, b],
            reloadOptions: new ModuleReloadOptions { ForceCancelInFlight = false },
            settlementOptions: null,
            inFlight: inFlight.Object);

        await handler.ReloadAsync(new ConfigChange { ChangedSections = ["Boot"] }, TestContext.Current.CancellationToken);
        lifecycle.IsActivated("b").Should().BeTrue();
        handler.PendingDeactivate.Should().Contain("b");
        b.DeactivateCount.Should().Be(0);

        stillInFlight = false;
        await WaitUntilAsync(() => b.DeactivateCount > 0, timeoutMs: 3000);
        lifecycle.IsActivated("b").Should().BeFalse();

        // —— 会话级：本轮快照后改 Scenario，只影响下一次结算 ——
        var sessionCatalog = CreateCatalog(
            processEnabled: ["git", "memory"],
            modules: [Desc("git", ["git_status"]), Desc("memory", ["memory_search"])]);
        var session = SessionData.Create(scenario: "code");
        var roundSnapshot = SessionSettlement.Compute(
            session, sessionCatalog, processScenario: "full", userToolsDisabled: null);

        session.Scenario = "work";
        roundSnapshot.ScenarioName.Should().Be("code");
        roundSnapshot.SettledToolIds.Should().Contain("git_status");
        roundSnapshot.SettledToolIds.Should().NotContain("memory_search");

        var nextRound = SessionSettlement.Compute(
            session, sessionCatalog, processScenario: "full", userToolsDisabled: null);
        nextRound.ScenarioName.Should().Be("work");
        nextRound.SettledToolIds.Should().Contain("memory_search");
        nextRound.SettledToolIds.Should().NotContain("git_status");
    }

    private static (IReadOnlyList<string> Rebuilt, IReadOnlyList<string> Missing) ReplayIntersect(
        IReadOnlyList<string> snapshotToolIds,
        IReadOnlySet<string> currentlyAvailable)
    {
        var rebuilt = new List<string>();
        var missing = new List<string>();
        foreach (var id in snapshotToolIds)
        {
            if (currentlyAvailable.Contains(id))
                rebuilt.Add(id);
            else
                missing.Add(id);
        }

        return (rebuilt, missing);
    }

    private static ModuleCatalog CreateCatalog(
        IReadOnlyList<string> processEnabled,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(modules);
        catalog.ReplaceEnabled(processEnabled);
        return catalog;
    }

    private static ModuleDescriptor Desc(
        string id,
        IReadOnlyList<string>? tools = null,
        string[]? dependsOn = null) =>
        new(id, tools ?? Array.Empty<string>(), Array.Empty<string>(), dependsOn ?? []);

    private static Seeing.Agent.Core.Tools.ToolManager CreateToolManager(params string[] ids)
    {
        var manager = new Seeing.Agent.Core.Tools.ToolManager(
            NullLogger<Seeing.Agent.Core.Tools.ToolManager>.Instance,
            new Seeing.Agent.Core.Hooks.HookManager(NullLogger<Seeing.Agent.Core.Hooks.HookManager>.Instance));
        foreach (var id in ids)
            manager.RegisterTool(new NamedTool(id));
        return manager;
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("WaitUntil condition not met");
            await Task.Delay(20);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(
        SessionData session,
        IExecutionEventPublisher publisher,
        IAgentExecutor executor,
        IReadOnlyList<TrackingModule>? modules = null)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns(session);
        sessionManager.Setup(m => m.SaveAsync(session.Id)).Returns(Task.CompletedTask);

        var instructionManager = new Mock<IInstructionManager>();
        instructionManager.Setup(m => m.InjectIfNeededAsync(
                It.IsAny<SessionData>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstructionInjectResult { Injected = false });

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.GetSessionModelRef(It.IsAny<SessionData>())).Returns(string.Empty);
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("m");
        modelManager.Setup(m => m.ResolveAcpModel(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "general", Runtime = AgentRuntime.Native });

        var runtimeManager = new Mock<IAgentRuntimeManager>();
        runtimeManager.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("general");

        var toolManager = CreateToolManager("git_status", "memory_search", "read", "current_time");

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(
        [
            new ModuleDescriptor("git", ["git_status"], [], []),
            new ModuleDescriptor("memory", ["memory_search"], [], []),
            new ModuleDescriptor("filesystem", ["read"], [], []),
            new ModuleDescriptor("basic", ["current_time"], [], [])
        ]);
        catalog.ReplaceEnabled(["git", "memory", "filesystem", "basic"]);

        ModuleLifecycleManager? lifecycle = null;
        if (modules is { Count: > 0 })
        {
            catalog.ReplaceAvailable(SettlementEngine.ToDescriptors(modules));
            catalog.ReplaceEnabled(modules.Select(m => m.Id).ToArray());
            lifecycle = new ModuleLifecycleManager(
                catalog, modules, new ServiceCollection().BuildServiceProvider(), NullLogger<ModuleLifecycleManager>.Instance);
            await lifecycle.ActivateAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(instructionManager.Object);
        services.AddSingleton(modelManager.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(executor);
        services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w =>
            w.ProjectSeeingDirectory == Path.Combine("workspace-root", ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(w => w.Cwd == "workspace-root"));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        services.AddSingleton<IToolManager>(toolManager);
        services.AddSingleton(toolManager);
        services.AddSingleton<IModuleCatalog>(catalog);
        services.AddSingleton(new ProcessSettlementOptions { HostDefaultScenario = "full" });
        services.AddSingleton<IPromptSectionContributor>(new FakeSectionContributor());
        if (lifecycle is not null)
            services.AddSingleton(lifecycle);

        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore.Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        var service = new ExecutionJobService(
            provider,
            publisher,
            new ExecutionOptions(),
            Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == new SeeingAgentOptions()),
            configStore.Object,
            NullLogger<ExecutionJobService>.Instance,
            new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()));

        return new Fixture(service, provider, lifecycle);
    }

    private sealed class Fixture(
        ExecutionJobService service,
        ServiceProvider provider,
        ModuleLifecycleManager? lifecycle) : IDisposable
    {
        public ExecutionJobService Service { get; } = service;
        public ModuleLifecycleManager? Lifecycle { get; } = lifecycle;

        public void Dispose()
        {
            Service.Dispose();
            provider.Dispose();
        }
    }

    private sealed class NamedTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => Id;
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
        public int Order => 0;
        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("tools");
    }

    private sealed class MutableOptions(SeeingAgentOptions current) : IOptionsMonitor<SeeingAgentOptions>
    {
        public SeeingAgentOptions CurrentValue { get; set; } = current;
        public SeeingAgentOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SeeingAgentOptions, string?> listener) => null;
    }

    private sealed class TrackingModule : ISeeingModule
    {
        public TrackingModule(string id, IReadOnlyList<string>? tools = null)
        {
            Id = id;
            ProvidedTools = tools ?? [];
        }

        public string Id { get; }
        public IReadOnlyList<string> ProvidedTools { get; }
        public IReadOnlyList<string> ProvidedSeams { get; } = [];
        public IReadOnlyList<string> DependsOn { get; } = [];
        public int ActivateCount { get; private set; }
        public int DeactivateCount { get; private set; }
        public void ConfigureServices(IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            ActivateCount++;
            return Task.CompletedTask;
        }
        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            DeactivateCount++;
            return Task.CompletedTask;
        }
    }
}
