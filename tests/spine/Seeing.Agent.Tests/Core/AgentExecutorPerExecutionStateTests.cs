using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// X5：AgentExecutor 为 Singleton，执行期可变状态（TodoEmpty/Incomplete 提醒标志等）
/// 必须 per-execution 隔离，避免并发 Loop 互相踩踏。
/// </summary>
public class AgentExecutorPerExecutionStateTests
{
    /// <summary>
    /// 通过门控强制两个 Loop 交错，确定性复现共享字段竞态：
    /// <list type="number">
    /// <item>A 跑到 step2，注入 TodoEmpty 提醒并将标志置 true，随后在该步 LLM 处阻塞；</item>
    /// <item>B 启动，其入口重置共享标志为 false 后阻塞在 step0；</item>
    /// <item>释放 A：A 的 step3 复查标志。若标志被 B 重置，A 会重复注入提醒；</item>
    /// <item>释放 B 完成。</item>
    /// </list>
    /// per-execution 状态下，A、B 各自应恰好注入一次提醒。
    /// </summary>
    [Fact]
    public async Task ConcurrentLoops_ShouldNotInterfere_OnTodoEmptyReminderFlag()
    {
        var callCounts = new ConcurrentDictionary<string, int>();
        var lastRequests = new ConcurrentDictionary<string, List<ChatMessage>>();

        var aAtStep2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bAtStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest req, string? sid, CancellationToken _) =>
            {
                var n = callCounts.AddOrUpdate(sid!, 1, (_, v) => v + 1);
                lastRequests[sid!] = req.Messages.ToList();

                if (sid == "A")
                {
                    // step2（第 3 次）：提醒已注入并置位，阻塞等待外部放行。
                    if (n == 3)
                    {
                        aAtStep2.TrySetResult();
                        return StreamAfterGate(gateA.Task, "loop_probe", "{}");
                    }
                    return n < 3 ? StreamToolCall("loop_probe", "{}") : StreamPlainCompletion();
                }

                if (sid == "B")
                {
                    // step0（入口重置后立即阻塞，尚未到达 step2）。
                    if (n == 1)
                    {
                        bAtStart.TrySetResult();
                        return StreamAfterGate(gateB.Task, "loop_probe", "{\"b\":1}");
                    }
                    return n < 4 ? StreamToolCall("loop_probe", $"{{\"b\":{n}}}") : StreamPlainCompletion();
                }

                return StreamPlainCompletion();
            });

        var todoStore = new Mock<ITodoStore>();
        todoStore.Setup(t => t.LoadAsync(It.IsAny<string>()))
            .ReturnsAsync((string sid) => new TodoList { SessionId = sid, Items = new() });

        var executor = await CreateExecutorAsync(llm.Object, todoStore.Object);

        var aTask = RunOne(executor, "A");
        await aAtStep2.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var bTask = RunOne(executor, "B");
        await bAtStart.Task.WaitAsync(TimeSpan.FromSeconds(10));

        gateA.TrySetResult();
        await aTask.WaitAsync(TimeSpan.FromSeconds(10));

        gateB.TrySetResult();
        await bTask.WaitAsync(TimeSpan.FromSeconds(10));

        CountTodoEmptyReminders(lastRequests["A"]).Should().Be(1, "会话 A 应恰好收到一次 TodoEmpty 提醒");
        CountTodoEmptyReminders(lastRequests["B"]).Should().Be(1, "会话 B 应恰好收到一次 TodoEmpty 提醒");
    }

    private static int CountTodoEmptyReminders(IReadOnlyList<ChatMessage> messages) =>
        messages.Count(m => m.Content != null && m.Content.Contains("kind=\"todo_empty\""));

    private static async Task RunOne(AgentExecutor executor, string sessionId)
    {
        var agent = new AgentDefinition
        {
            Name = "state-agent",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = 10
        };
        var context = new AgentContext
        {
            SessionId = sessionId,
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root"
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        await foreach (var _ in executor.ExecuteAsync(agent, messages, context, TestContext.Current.CancellationToken))
        {
        }
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamToolCall(string name, string arguments)
    {
        yield return ToolCallUpdate(name, arguments);
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamAfterGate(Task gate, string name, string arguments)
    {
        await gate.ConfigureAwait(false);
        yield return ToolCallUpdate(name, arguments);
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamPlainCompletion()
    {
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop" };
        await Task.CompletedTask;
    }

    private static StreamUpdate ToolCallUpdate(string name, string arguments) => new()
    {
        IsComplete = true,
        FinishReason = "tool_calls",
        ToolCallDeltas = new List<ToolCall>
        {
            new()
            {
                Id = "c1",
                Type = "function",
                Function = new FunctionCall { Name = name, Arguments = arguments }
            }
        }
    };

    private static async Task<AgentExecutor> CreateExecutorAsync(ILlmService llm, ITodoStore todoStore)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        await toolManager.RegisterToolAsync(new NoopTool());

        var agentRegistry = new Mock<IAgentRegistry>();

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
            agentRegistry.Object,
            new PromptBuilder(Array.Empty<IPromptSectionContributor>()),
            modelManager.Object,
            NullLogger<AgentExecutor>.Instance,
            sessionManager: null,
            loopScheduler: null,
            todoStore: todoStore,
            turnRetryPolicy: null);
    }

    private sealed class NoopTool : ITool
    {
        public string Id => "loop_probe";
        public string Description => "noop";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(new ToolResult { Success = true, Output = "ok" });
    }
}
