using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Llm;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// 执行层可见集校验：本会话本轮未下发 schema 的工具不得被调用。
/// </summary>
public class AgentExecutorToolVisibilityTests
{
    [Fact]
    public async Task ExecuteAsync_InlineTool_ShouldNotBeRejectedByVisibilityGate()
    {
        var executor = CreateExecutor("visible_tool");
        var settlement = BuildSettlement(
            ("visible_tool", ToolExposureMode.Inline));

        var events = await RunAsync(executor, settlement);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().NotBe(ToolCallStatus.Rejected);
    }

    [Fact]
    public async Task ExecuteAsync_DeferredTool_ShouldRejectAndPointToSearch()
    {
        var executor = CreateExecutor("hidden_tool");
        var settlement = BuildSettlement(
            ("visible_tool", ToolExposureMode.Inline),
            ("hidden_tool", ToolExposureMode.Deferred));

        var events = await RunAsync(executor, settlement);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().Be(ToolCallStatus.Rejected);
        complete.Error.Should().Contain(ToolIdConventions.SearchToolId);
        complete.Error.Should().Contain("本次执行");
    }

    [Fact]
    public async Task ExecuteAsync_DeferredToolDeclaredLoadedInSameExecution_ShouldBeAllowed()
    {
        // 同一执行内 tool_search 声明加载的工具：schema 已随工具结果进入对话，
        // 执行器应用声明后必须放行，否则模型只能重复检索或等用户再发消息。
        var llm = CreateScriptedLlm(ToolIdConventions.SearchToolId, "hidden_tool");
        var executor = await CreateExecutorWithToolsAsync(
            llm, new DeclaringSearchTool(), new NamedTool("hidden_tool"));
        var settlement = BuildSettlement(
            (ToolIdConventions.SearchToolId, ToolExposureMode.Inline),
            ("hidden_tool", ToolExposureMode.Deferred));

        var events = await RunAsync(executor, settlement, maxSteps: 3);

        var completes = events.OfType<ToolCallEvent>()
            .Where(e => e.Type == MessageEventType.ToolCallComplete)
            .ToList();

        var searchComplete = completes.Single(e => e.ToolName == ToolIdConventions.SearchToolId);
        searchComplete.Status.Should().NotBe(ToolCallStatus.Rejected);
        searchComplete.Metadata.Should().NotBeNull();
        searchComplete.Metadata!["exposure.loaded"].Should().BeEquivalentTo(new[] { "hidden_tool" });

        completes.Single(e => e.ToolName == "hidden_tool")
            .Status.Should().NotBe(ToolCallStatus.Rejected);
    }

    [Fact]
    public async Task ExecuteAsync_LoadDeclaredWithoutCapability_ShouldBeIgnored()
    {
        // 未声明 exposure.load 的工具不得解锁其他工具（防止第三方工具绕过“先检索”约定）。
        var llm = CreateScriptedLlm(ToolIdConventions.SearchToolId, "hidden_tool");
        var executor = await CreateExecutorWithToolsAsync(
            llm, new UndeclaredSearchTool(), new NamedTool("hidden_tool"));
        var settlement = BuildSettlement(
            (ToolIdConventions.SearchToolId, ToolExposureMode.Inline),
            ("hidden_tool", ToolExposureMode.Deferred));

        var events = await RunAsync(executor, settlement, maxSteps: 3);

        var completes = events.OfType<ToolCallEvent>()
            .Where(e => e.Type == MessageEventType.ToolCallComplete)
            .ToList();

        completes.Single(e => e.ToolName == ToolIdConventions.SearchToolId)
            .Status.Should().NotBe(ToolCallStatus.Rejected);
        completes.Single(e => e.ToolName == "hidden_tool")
            .Status.Should().Be(ToolCallStatus.Rejected);
    }

    [Fact]
    public async Task ExecuteAsync_ToolAbsentFromSettlement_ShouldRejectWithoutSearchHint()
    {
        var executor = CreateExecutor("never_exposed_tool");
        var settlement = BuildSettlement(
            ("visible_tool", ToolExposureMode.Inline));

        var events = await RunAsync(executor, settlement);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().Be(ToolCallStatus.Rejected);
        complete.Error.Should().NotContain(ToolIdConventions.SearchToolId);
        complete.Error.Should().Contain("不可用");
    }

    [Fact]
    public async Task ExecuteAsync_NullSettlement_ShouldExposeNullToolScope()
    {
        // 无结算 = 无执行级放行能力：工具必须看到 null 作用域，
        // 否则检索工具会误判"支持同执行放行"并把语料当成空集。
        var executor = await CreateExecutorWithToolsAsync(
            CreateScriptedLlm("probe_tool"), new ScopeProbeTool());

        var events = await RunAsync(executor, settlement: null, maxSteps: 1);

        events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete)
            .Status.Should().NotBe(ToolCallStatus.Rejected);
    }

    [Fact]
    public async Task ExecuteAsync_LoadDeclaredWithExplicitFalseCapability_ShouldBeIgnored()
    {
        // 能力值写 "false" 也必须视为不可（fail-closed），不能因为“键存在”就放行。
        var llm = CreateScriptedLlm(ToolIdConventions.SearchToolId, "hidden_tool");
        var executor = await CreateExecutorWithToolsAsync(
            llm, new FalseCapabilitySearchTool(), new NamedTool("hidden_tool"));
        var settlement = BuildSettlement(
            (ToolIdConventions.SearchToolId, ToolExposureMode.Inline),
            ("hidden_tool", ToolExposureMode.Deferred));

        var events = await RunAsync(executor, settlement, maxSteps: 3);

        events.OfType<ToolCallEvent>()
            .Where(e => e.Type == MessageEventType.ToolCallComplete)
            .Single(e => e.ToolName == "hidden_tool")
            .Status.Should().Be(ToolCallStatus.Rejected);
    }

    [Fact]
    public async Task ExecuteAsync_NullMetadataFromTool_ShouldNotFailSuccessfulCall()
    {
        // 第三方工具把 Metadata 置 null 时，审计写入不得把成功调用翻成失败。
        var llm = CreateScriptedLlm(ToolIdConventions.SearchToolId, "hidden_tool");
        var executor = await CreateExecutorWithToolsAsync(
            llm, new NullMetadataSearchTool(), new NamedTool("hidden_tool"));
        var settlement = BuildSettlement(
            (ToolIdConventions.SearchToolId, ToolExposureMode.Inline),
            ("hidden_tool", ToolExposureMode.Deferred));

        var events = await RunAsync(executor, settlement, maxSteps: 3);

        var searchComplete = events.OfType<ToolCallEvent>()
            .Where(e => e.Type == MessageEventType.ToolCallComplete)
            .Single(e => e.ToolName == ToolIdConventions.SearchToolId);
        searchComplete.Status.Should().Be(ToolCallStatus.Success);
        searchComplete.Metadata!["exposure.loaded"].Should().BeEquivalentTo(new[] { "hidden_tool" });
    }

    [Fact]
    public async Task ExecuteAsync_NullSettlement_ShouldNotGate()
    {
        var executor = CreateExecutor("any_tool");

        var events = await RunAsync(executor, settlement: null);

        var complete = events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
        complete.Status.Should().NotBe(ToolCallStatus.Rejected);
    }

    private static SessionToolSchemaResolution BuildSettlement(
        params (string Id, ToolExposureMode Mode)[] tools)
    {
        var exposed = tools
            .Select(t => new ExposedTool(
                t.Id,
                Namespace: string.Empty,
                Signature: t.Id,
                Schema: new FunctionToolSchema
                {
                    Function = new FunctionSchema { Name = t.Id, Description = t.Id }
                },
                EstimatedTokens: 10,
                Mode: t.Mode))
            .ToList();

        return new SessionToolSchemaResolution(
            exposed,
            Array.Empty<string>(),
            ToolExposureLayout.Tiered,
            CatalogText: null,
            SchemaTokens: 10,
            CatalogTokens: 0);
    }

    private static async Task<List<IMessageEvent>> RunAsync(
        AgentExecutor executor,
        SessionToolSchemaResolution? settlement,
        int maxSteps = 1)
    {
        var agent = new AgentDefinition
        {
            Name = "visibility-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = maxSteps
        };
        var context = new AgentContext
        {
            SessionId = "s1",
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root",
            ToolSettlement = settlement
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        var events = new List<IMessageEvent>();
        await foreach (var evt in executor.ExecuteAsync(
            agent, messages, context, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    private static AgentExecutor CreateExecutor(string callTool = "visible_tool")
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest _, string? _, CancellationToken _) => StreamToolCallCompletion(callTool));

        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        return CreateExecutorCore(llm.Object, toolManager, hookManager);
    }

    private static async Task<AgentExecutor> CreateExecutorWithToolsAsync(
        ILlmService llm, params ITool[] tools)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        foreach (var tool in tools)
            await toolManager.RegisterToolAsync(tool);

        return CreateExecutorCore(llm, toolManager, hookManager);
    }

    private static AgentExecutor CreateExecutorCore(
        ILlmService llm, ToolManager toolManager, HookManager hookManager)
    {
        var permission = new Mock<IPermissionService>();
        permission.Setup(p => p.EvaluateToolAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<PermissionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionResult.Allow(default, "test-allow"));

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("test-model");

        return new AgentExecutor(
            llm,
            toolManager,
            permission.Object,
            hookManager,
            new Mock<IAgentRegistry>().Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()),
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance);
    }

    /// <summary>按步骤返回工具调用的脚本化 LLM：脚本用尽后返回普通文本完成。</summary>
    private static ILlmService CreateScriptedLlm(params string[] toolNames)
    {
        var step = 0;
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var index = Interlocked.Increment(ref step) - 1;
                return index < toolNames.Length
                    ? StreamToolCallCompletion(toolNames[index])
                    : StreamTextCompletion();
            });

        return llm.Object;
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamToolCallCompletion(string toolName)
    {
        await Task.Yield();
        yield return new StreamUpdate
        {
            IsComplete = true,
            FinishReason = "tool_calls",
            ToolCallDeltas = new List<ToolCall>
            {
                new()
                {
                    Id = "c1",
                    Type = "function",
                    Function = new FunctionCall { Name = toolName, Arguments = "{}" }
                }
            }
        };
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamTextCompletion()
    {
        await Task.Yield();
        yield return new StreamUpdate
        {
            IsComplete = true,
            FinishReason = "stop",
            ContentDelta = "done"
        };
    }

    /// <summary>模拟声明式检索工具：声明 exposure.load 并通过 LoadedToolIds 上报。</summary>
    [ToolCapability(ToolCapabilityKeys.ExposureLoad, "true")]
    private sealed class DeclaringSearchTool : ITool
    {
        public string Id => ToolIdConventions.SearchToolId;
        public string Description => "search";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            context.ToolScope.Should().NotBeNull("执行器必须向工具透传执行级作用域");
            var result = ToolResult.Succeeded("loaded");
            result.LoadedToolIds = ["hidden_tool"];
            return Task.FromResult(result);
        }
    }

    /// <summary>同形工具但未声明 exposure.load：其加载声明必须被忽略。</summary>
    private sealed class UndeclaredSearchTool : ITool
    {
        public string Id => ToolIdConventions.SearchToolId;
        public string Description => "search";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var result = ToolResult.Succeeded("loaded");
            result.LoadedToolIds = ["hidden_tool"];
            return Task.FromResult(result);
        }
    }

    /// <summary>探针：断言无结算时执行器不得向工具暴露作用域。</summary>
    private sealed class ScopeProbeTool : ITool
    {
        public string Id => "probe_tool";
        public string Description => "probe";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            context.ToolScope.Should().BeNull("无结算时不存在执行级作用域");
            return Task.FromResult(ToolResult.Succeeded("probe"));
        }
    }

    /// <summary>显式声明 exposure.load=false：加载声明必须被忽略（fail-closed）。</summary>
    [ToolCapability(ToolCapabilityKeys.ExposureLoad, "false")]
    private sealed class FalseCapabilitySearchTool : ITool
    {
        public string Id => ToolIdConventions.SearchToolId;
        public string Description => "search";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var result = ToolResult.Succeeded("loaded");
            result.LoadedToolIds = ["hidden_tool"];
            return Task.FromResult(result);
        }
    }

    /// <summary>声明能力但把 Metadata 置 null：审计写入不得抛异常。</summary>
    [ToolCapability(ToolCapabilityKeys.ExposureLoad, "true")]
    private sealed class NullMetadataSearchTool : ITool
    {
        public string Id => ToolIdConventions.SearchToolId;
        public string Description => "search";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var result = ToolResult.Succeeded("loaded");
            result.Metadata = null!;
            result.LoadedToolIds = ["hidden_tool"];
            return Task.FromResult(result);
        }
    }

    private sealed class NamedTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => id;
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(ToolResult.Succeeded("ok"));
    }
}
