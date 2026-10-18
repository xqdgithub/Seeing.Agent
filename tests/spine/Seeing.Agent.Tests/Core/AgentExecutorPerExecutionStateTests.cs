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
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// X5：AgentExecutor 为 Singleton，执行期可变状态（Incomplete 提醒标志等）
/// 必须 per-execution 隔离，避免并发 Loop 互相踩踏。
/// </summary>
public class AgentExecutorPerExecutionStateTests
{
    /// <summary>
    /// A 在未完成拦截后的第二次模型调用处阻塞（标志已置位）。
    /// 此时启动 B 并让它跑完。若标志被共享，B 会跳过自己的那一次拦截。
    /// </summary>
    [Fact]
    public async Task ConcurrentLoops_ShouldNotInterfere_OnIncompleteReminderFlag()
    {
        var callCounts = new ConcurrentDictionary<string, int>();
        var requests = new ConcurrentDictionary<string, List<List<ChatMessage>>>();

        var aAtReminder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest req, string? sid, CancellationToken _) =>
            {
                var n = callCounts.AddOrUpdate(sid!, 1, (_, v) => v + 1);
                var bucket = requests.GetOrAdd(sid!, _ => new List<List<ChatMessage>>());
                lock (bucket)
                    bucket.Add(req.Messages.ToList());

                if (sid == "A" && n == 2)
                {
                    aAtReminder.TrySetResult();
                    return StreamPlainAfterGate(gateA.Task);
                }

                return StreamPlainCompletion();
            });

        var todoStore = new Mock<ITodoStore>();
        todoStore.Setup(t => t.LoadAsync(It.IsAny<string>()))
            .ReturnsAsync((string sid) => new TodoList
            {
                SessionId = sid,
                Items = [new TodoItem { Id = "1", Content = "finish", Status = TodoStatus.Pending }]
            });

        var executor = await CreateExecutorAsync(llm.Object, todoStore.Object);

        var aTask = RunOne(executor, "A");
        await aAtReminder.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await RunOne(executor, "B").WaitAsync(TimeSpan.FromSeconds(10));

        gateA.TrySetResult();
        await aTask.WaitAsync(TimeSpan.FromSeconds(10));

        CountIncompleteReminders(requests["A"]).Should().Be(1, "会话 A 应恰好收到一次未完成提醒");
        CountIncompleteReminders(requests["B"]).Should().Be(1, "会话 B 应恰好收到一次未完成提醒");
    }

    private static int CountIncompleteReminders(List<List<ChatMessage>> calls) =>
        calls.Sum(messages => messages.Count(m => m.Content != null && m.Content.Contains("kind=\"todo_incomplete\"")));

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
            WorkspaceRoot = "workspace-root",
            ToolSchemas = [new FunctionToolSchema { Function = new FunctionSchema { Name = "todowrite" } }]
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "hi" } };

        await foreach (var _ in executor.ExecuteAsync(agent, messages, context, TestContext.Current.CancellationToken))
        {
        }
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamPlainAfterGate(Task gate)
    {
        await gate.ConfigureAwait(false);
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop" };
    }

    private static async IAsyncEnumerable<StreamUpdate> StreamPlainCompletion()
    {
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop" };
        await Task.CompletedTask;
    }

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
