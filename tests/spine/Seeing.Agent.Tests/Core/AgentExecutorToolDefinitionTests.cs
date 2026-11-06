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
using Seeing.Agent.Tools.Support;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// 参数类失败时回灌工具完整定义：只在「本轮未下发 schema 且本执行未加载」且失败属参数类时附，
/// 超时/重试耗尽/权限拒绝等噪声失败不附。
/// </summary>
public class AgentExecutorToolDefinitionTests
{
    private const string TargetTool = "cron_create";

    [Fact]
    public async Task 延迟工具普通失败_应附带定义()
    {
        var complete = await RunSingleAsync(FailingBehavior.ReturnFailure);

        complete.Status.Should().Be(ToolCallStatus.Failed);
        complete.ToolDefinition.Should().NotBeNull();
        complete.ToolDefinition!.Should().Contain(TargetTool);
        complete.ToolDefinition.Should().Contain("parameters");
    }

    [Fact]
    public async Task 延迟工具抛异常_应附带定义()
    {
        var complete = await RunSingleAsync(FailingBehavior.ThrowJsonException);

        complete.Status.Should().Be(ToolCallStatus.Failed);
        complete.ToolDefinition.Should().NotBeNull(
            "抛 JsonException 的工具（如 GitCommitTool 缺参）走 catch 出口，也必须拿到定义");
    }

    [Fact]
    public async Task 超时失败_不应附带定义()
    {
        var complete = await RunSingleAsync(FailingBehavior.Timeout);

        complete.Status.Should().Be(ToolCallStatus.Failed);
        complete.ToolDefinition.Should().BeNull();
    }

    [Fact]
    public async Task 重试耗尽_不应附带定义()
    {
        var complete = await RunSingleAsync(FailingBehavior.RetryExhausted);

        complete.ToolDefinition.Should().BeNull();
    }

    [Fact]
    public async Task 权限拒绝_不应附带定义()
    {
        var complete = await RunSingleAsync(FailingBehavior.PermissionDenied);

        complete.ToolDefinition.Should().BeNull();
    }

    [Fact]
    public async Task 策略给出的拒绝原因_也不应附带定义()
    {
        // 真实拒绝的 Error 是策略原因（文案千变万化），不能靠字符串比较识别。
        var complete = await RunSingleAsync(FailingBehavior.PolicyDeniedWithReason);

        complete.ToolDefinition.Should().BeNull();
    }

    [Fact]
    public async Task IO异常_不应附带定义()
    {
        var complete = await RunSingleAsync(FailingBehavior.ThrowIoException);

        complete.ToolDefinition.Should().BeNull("IO 异常与参数无关，附定义只会浪费上下文");
    }

    [Fact]
    public async Task 内联工具失败_不应附带定义()
    {
        var complete = await RunSingleAsync(FailingBehavior.ReturnFailure, inline: true);

        complete.ToolDefinition.Should().BeNull("内联工具本来就有 schema，不需要回灌");
    }

    [Fact]
    public async Task 已加载工具失败_不应附带定义()
    {
        // detail=full 已给过定义；该工具在结算里仍是 Deferred，只看 SendsSchema 会重复附。
        var searchTool = new DeclaringSearchTool(TargetTool);
        var targetTool = new FailingTool(TargetTool, FailingBehavior.ReturnFailure);

        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        await toolManager.RegisterToolAsync(searchTool);
        await toolManager.RegisterToolAsync(targetTool);

        var executor = CreateExecutor(
            CreateScriptedLlm(ToolIdConventions.SearchToolId, TargetTool), toolManager, hookManager);
        var settlement = BuildSettlement(ToolExposureMode.Deferred, oversized: false, includeSearchTool: true);

        var events = await RunAsync(executor, settlement, maxSteps: 3);

        events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete && e.ToolName == TargetTool)
            .ToolDefinition.Should().BeNull("detail=full 已返回过定义，不得重复附");
    }

    [Fact]
    public async Task 超限定义_应回退为提示且不留空承诺()
    {
        var complete = await RunSingleAsync(FailingBehavior.ReturnFailure, oversized: true);

        complete.ToolDefinition.Should().BeNull();
        complete.Error.Should().Contain("未能附上该工具的完整定义");
        // 本结算不含检索入口，提示不得指向一个不存在的工具。
        complete.Error.Should().NotContain(ToolIdConventions.SearchToolId);
    }

    [Fact]
    public async Task 超限定义_有检索入口_应提示用检索取定义()
    {
        var complete = await RunSingleAsync(
            FailingBehavior.ReturnFailure, oversized: true, includeSearchTool: true);

        complete.ToolDefinition.Should().BeNull();
        complete.Error.Should().Contain(ToolIdConventions.SearchToolId);
    }

    // ===== 脚手架 =====

    private enum FailingBehavior
    {
        ReturnFailure,
        ThrowJsonException,
        ThrowIoException,
        Timeout,
        RetryExhausted,
        PermissionDenied,
        PolicyDeniedWithReason
    }

    private static async Task<ToolCallEvent> RunSingleAsync(
        FailingBehavior failure, bool inline = false, bool oversized = false,
        bool includeSearchTool = false)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        await toolManager.RegisterToolAsync(new FailingTool(TargetTool, failure));

        var executor = CreateExecutor(CreateScriptedLlm(TargetTool), toolManager, hookManager);
        var mode = inline ? ToolExposureMode.Inline : ToolExposureMode.Deferred;
        var settlement = BuildSettlement(mode, oversized, includeSearchTool);

        var events = await RunAsync(executor, settlement, maxSteps: 1);
        return events.OfType<ToolCallEvent>()
            .Single(e => e.Type == MessageEventType.ToolCallComplete);
    }

    private static SessionToolSchemaResolution BuildSettlement(
        ToolExposureMode mode, bool oversized, bool includeSearchTool)
    {
        var parameters = oversized
            ? JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    filler = new { type = "string", description = new string('x', 9000) }
                }
            })
            : JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { taskType = new { type = "string" } },
                required = new[] { "taskType" }
            });

        var tools = new List<ExposedTool>
        {
            new(
                TargetTool,
                Namespace: string.Empty,
                Signature: $"{TargetTool}(taskType)",
                Schema: new FunctionToolSchema
                {
                    Function = new FunctionSchema
                    {
                        Name = TargetTool,
                        Description = "创建定时任务",
                        Parameters = parameters
                    }
                },
                EstimatedTokens: 50,
                Mode: mode)
        };

        if (includeSearchTool)
        {
            tools.Add(new ExposedTool(
                ToolIdConventions.SearchToolId,
                Namespace: string.Empty,
                Signature: $"{ToolIdConventions.SearchToolId}(query?)",
                Schema: new FunctionToolSchema
                {
                    Function = new FunctionSchema
                    {
                        Name = ToolIdConventions.SearchToolId,
                        Description = "检索工具"
                    }
                },
                EstimatedTokens: 20,
                Mode: ToolExposureMode.Inline));
        }

        return new SessionToolSchemaResolution(
            tools,
            Array.Empty<string>(),
            ToolExposureLayout.Tiered,
            CatalogText: null,
            SchemaTokens: 50,
            CatalogTokens: 0);
    }

    private static async Task<List<IMessageEvent>> RunAsync(
        AgentExecutor executor, SessionToolSchemaResolution settlement, int maxSteps)
    {
        var agent = new AgentDefinition
        {
            Name = "definition-agent",
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

    private static AgentExecutor CreateExecutor(
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
                    Id = "c_" + toolName,
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

    /// <summary>按指定方式失败的工具。</summary>
    private sealed class FailingTool : ToolBase
    {
        private readonly FailingBehavior _behavior;

        public FailingTool(string id, FailingBehavior behavior)
            : base(NullLogger<FailingTool>.Instance)
        {
            Id = id;
            _behavior = behavior;
        }

        public override string Id { get; }
        public override string Description => "创建定时任务";

        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { taskType = new { type = "string" } },
            required = new[] { "taskType" }
        });

        public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            switch (_behavior)
            {
                case FailingBehavior.ThrowJsonException:
                    throw new JsonException("Missing required parameter: taskType");
                case FailingBehavior.ThrowIoException:
                    throw new IOException("磁盘不可读");
                case FailingBehavior.Timeout:
                    return Task.FromResult(new ToolResult
                    {
                        Success = false,
                        Title = "执行超时",
                        Error = "执行超时",
                        Metadata = new Dictionary<string, object> { ["timeout"] = true }
                    });
                case FailingBehavior.RetryExhausted:
                    return Task.FromResult(new ToolResult
                    {
                        Success = false,
                        Title = "重试耗尽",
                        Error = "重试耗尽"
                    });
                case FailingBehavior.PermissionDenied:
                    return Task.FromResult(new ToolResult
                    {
                        Success = false,
                        Title = "权限拒绝",
                        Error = "Permission denied",
                        Metadata = new Dictionary<string, object> { ["permission.denied"] = true }
                    });
                case FailingBehavior.PolicyDeniedWithReason:
                    // 生产路径的真实形状：Error 是策略原因，只有结构化标记能识别。
                    return Task.FromResult(new ToolResult
                    {
                        Success = false,
                        Title = "权限拒绝",
                        Error = "Denied by rule shell.execute from project: shell.execute",
                        Metadata = new Dictionary<string, object> { ["permission.denied"] = true }
                    });
                default:
                    return Task.FromResult(new ToolResult
                    {
                        Success = false,
                        Error = "缺少必需参数: taskType"
                    });
            }
        }
    }

    /// <summary>声明 exposure.load 并通过 LoadedToolIds 上报的检索工具（模拟 tool_search detail=full）。</summary>
    [ToolCapability(ToolCapabilityKeys.ExposureLoad, "true")]
    private sealed class DeclaringSearchTool(string targetId) : ITool
    {
        public string Id => ToolIdConventions.SearchToolId;
        public string Description => "search";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var result = ToolResult.Succeeded("loaded");
            result.LoadedToolIds = [targetId];
            return Task.FromResult(result);
        }
    }
}
