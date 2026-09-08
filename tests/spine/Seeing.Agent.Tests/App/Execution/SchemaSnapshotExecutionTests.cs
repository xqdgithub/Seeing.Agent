using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Core.Modules;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// P7-T10：schema 唯一计算点 + SchemaSnapshotEvent 与 ChatRequest.Tools 同源。
/// </summary>
public class SchemaSnapshotExecutionTests
{
    [Fact]
    public void AgentExecutor_ShouldNotHavePrivateGetToolSchemas()
    {
        var method = typeof(AgentExecutor).GetMethod(
            "GetToolSchemas",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        method.Should().BeNull(
            "Native/AgentExecutor 不得自算 schema；应读 AgentContext.ToolSchemas");
    }

    [Fact]
    public async Task SubmitAsync_EmitsSchemaSnapshot_MatchingAgentContextToolSchemas()
    {
        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        AgentContext? capturedContext = null;
        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext ctx, CancellationToken _) =>
            {
                capturedContext = ctx;
                return EmptyStream();
            });

        var session = SessionData.Create(scenario: "code");
        using var fixture = CreateFixture(session, publisher.Object, executor.Object, withCatalog: true);

        var result = await fixture.Service.SubmitAsync(
            session.Id,
            ChatInput.FromText("hi"),
            new ChatOptions
            {
                AgentId = "general",
                SkipUserMessagePersist = true,
                SkipInstructionInject = true
            });

        result.Success.Should().BeTrue();

        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent c && c.ExecutionId == result.ExecutionId));

        var snapshot = published.Select(p => p.Event).OfType<SchemaSnapshotEvent>().Single();
        capturedContext.Should().NotBeNull();
        capturedContext!.ToolSchemas.Should().NotBeNull();

        var contextToolIds = capturedContext.ToolSchemas!
            .Where(s => s.Function != null)
            .Select(s => s.Function.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        snapshot.ToolIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Should().Equal(contextToolIds);

        snapshot.SessionId.Should().Be(session.Id);
        snapshot.ExecutionId.Should().Be(result.ExecutionId);
        snapshot.ToolIds.Should().Contain("git_status");
        snapshot.ToolIds.Should().NotContain("memory_search");
        snapshot.SectionIds.Should().Contain("tools");

        // ChatEventTracker 写入 SessionMessage.Metadata["schema_snapshot"]
        var metaMsg = session.Messages.FirstOrDefault(m =>
            m.Metadata != null && m.Metadata.ContainsKey(ChatEventTracker.SchemaSnapshotMetadataKey));
        metaMsg.Should().NotBeNull();
        var payload = metaMsg!.Metadata![ChatEventTracker.SchemaSnapshotMetadataKey]
            .Should().BeAssignableTo<IDictionary<string, object>>().Subject;
        payload["executionId"].Should().Be(result.ExecutionId);
    }

    [Fact]
    public void SessionMessage_Clone_DeepCopiesSchemaSnapshotMetadata()
    {
        var original = SessionMessage.SystemMessage(string.Empty);
        var toolIds = new List<string> { "git_status", "read" };
        original.Metadata = new Dictionary<string, object>
        {
            [ChatEventTracker.SchemaSnapshotMetadataKey] = new Dictionary<string, object>
            {
                ["executionId"] = "exec_1",
                ["toolIds"] = toolIds,
                ["sectionIds"] = new List<string> { "tools" }
            }
        };

        var clone = original.Clone();
        clone.Metadata.Should().NotBeSameAs(original.Metadata);
        var clonedSnap = (IDictionary<string, object>)clone.Metadata![ChatEventTracker.SchemaSnapshotMetadataKey];
        var originalSnap = (IDictionary<string, object>)original.Metadata![ChatEventTracker.SchemaSnapshotMetadataKey];
        clonedSnap.Should().NotBeSameAs(originalSnap);

        var clonedTools = (IList<string>)clonedSnap["toolIds"];
        clonedTools.Should().NotBeSameAs(toolIds);
        clonedTools.Add("mutated");
        toolIds.Should().NotContain("mutated");
    }

    [Fact]
    public async Task GetToolSchemasAsync_SettledIds_AppliesAgentLayer3()
    {
        var logger = NullLogger<Seeing.Agent.Core.Tools.ToolManager>.Instance;
        var hooks = new Seeing.Agent.Core.Hooks.HookManager(
            NullLogger<Seeing.Agent.Core.Hooks.HookManager>.Instance);
        var manager = new Seeing.Agent.Core.Tools.ToolManager(logger, hooks);
        manager.RegisterTool(new NamedTool("git_status"));
        manager.RegisterTool(new NamedTool("memory_search"));
        manager.RegisterTool(new NamedTool("read"));

        var agent = new AgentDefinition
        {
            Name = "t",
            DeniedTools = ["memory_search"]
        };

        var schemas = await manager.GetToolSchemasAsync(
            ["git_status", "memory_search", "read"],
            agent);

        schemas.Select(s => s.Function!.Name).Should().BeEquivalentTo(["git_status", "read"]);
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

    private static Fixture CreateFixture(
        SessionData session,
        IExecutionEventPublisher publisher,
        IAgentExecutor executor,
        bool withCatalog)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>()))
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

        var toolManager = new Seeing.Agent.Core.Tools.ToolManager(
            NullLogger<Seeing.Agent.Core.Tools.ToolManager>.Instance,
            new Seeing.Agent.Core.Hooks.HookManager(NullLogger<Seeing.Agent.Core.Hooks.HookManager>.Instance));
        toolManager.RegisterTool(new NamedTool("git_status"));
        toolManager.RegisterTool(new NamedTool("memory_search"));
        toolManager.RegisterTool(new NamedTool("read"));

        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(
        [
            new ModuleDescriptor("git", ["git_status"], [], []),
            new ModuleDescriptor("memory", ["memory_search"], [], []),
            new ModuleDescriptor("filesystem", ["read"], [], []),
            new ModuleDescriptor("basic", ["current_time"], [], [])
        ]);
        // 进程级启用 code+work 并集，会话级再收窄
        catalog.ReplaceEnabled(["git", "memory", "filesystem", "basic"]);

        var section = new FakeSectionContributor();

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(instructionManager.Object);
        services.AddSingleton(modelManager.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(executor);
        services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.ProjectSeeingDirectory == Path.Combine("workspace-root", ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(w => w.Cwd == "workspace-root"));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        services.AddSingleton<IToolManager>(toolManager);
        services.AddSingleton(toolManager);
        if (withCatalog)
            services.AddSingleton<IModuleCatalog>(catalog);
        services.AddSingleton(new ProcessSettlementOptions { HostDefaultScenario = "full" });
        services.AddSingleton<IPromptSectionContributor>(section);
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
            new CompactionRunner(new CompressionService(null!, Mock.Of<ISessionManager>()), Mock.Of<IExecutionEventPublisher>(), Mock.Of<ISessionManager>()));

        return new Fixture(service, provider);
    }

    private sealed class Fixture(ExecutionJobService service, ServiceProvider provider) : IDisposable
    {
        public ExecutionJobService Service { get; } = service;

        public void Dispose()
        {
            Service.Dispose();
            provider.Dispose();
        }
    }

    private sealed class NamedTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => id;
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
}
