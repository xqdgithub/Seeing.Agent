using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
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
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// Native 循环的 todo 编排：仅当本轮 schema 含 todowrite 时生效。
/// 暂停项在首步追加到消息末尾；未完成项在无工具退出和 EndTurn 上各拦一次。
/// </summary>
public class AgentExecutorTodoPolicyTests
{
    [Fact]
    public async Task PlainStop_WithIncompleteTodos_ShouldRemindOnceThenComplete()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var llm = Llm(requests, _ => Plain());

        var executor = await CreateExecutorAsync(llm.Object, Pending("s1"));
        var events = await RunAsync(executor, "s1", withTodoWrite: true, maxSteps: 5);

        requests.Should().HaveCount(2);
        CountKind(requests[0], "todo_incomplete").Should().Be(0);
        CountKind(requests[1], "todo_incomplete").Should().Be(1);
        requests[1].Should().Contain(m => m.Content != null && m.Content.Contains("说明为何现在停止"));
        events.OfType<LoopCompleteEvent>().Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task EndTurn_WithIncompleteTodos_ShouldHoldOnceThenAcceptSecondEndTurn()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var llm = Llm(requests, _ => ToolCall("end_turn_tool"));
        var reasons = new Queue<string>(new[] { "first", "second" });

        var executor = await CreateExecutorAsync(
            llm.Object,
            Pending("s1"),
            manager => manager.RegisterToolAsync(new EndTurnTool(() => reasons.Dequeue())));

        var events = await RunAsync(executor, "s1", withTodoWrite: true, maxSteps: 5);

        requests.Should().HaveCount(2);
        CountKind(requests[1], "todo_incomplete").Should().Be(1);
        var complete = events.OfType<LoopCompleteEvent>().Should().ContainSingle().Subject;
        complete.Success.Should().BeTrue();
        complete.Reason.Should().Be("second");
    }

    [Fact]
    public async Task EndTurn_WithoutTodoWriteSchema_ShouldCompleteImmediately()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var llm = Llm(requests, _ => ToolCall("end_turn_tool"));

        var executor = await CreateExecutorAsync(
            llm.Object,
            Pending("s1"),
            manager => manager.RegisterToolAsync(new EndTurnTool(() => "kept")));

        var events = await RunAsync(executor, "s1", withTodoWrite: false, maxSteps: 5);

        requests.Should().HaveCount(1);
        requests[0].Should().NotContain(m => m.Content != null && m.Content.Contains("todo_incomplete"));
        events.OfType<LoopCompleteEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be("kept");
    }

    [Fact]
    public async Task PausedTodos_ShouldAppendOnceToMessageSuffix_NotSystemPrompt()
    {
        var requests = new List<(string? SystemPrompt, List<ChatMessage> Messages)>();
        var llm = new Mock<ILlmService>();
        var call = 0;
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest req, string? _, CancellationToken _) =>
            {
                call++;
                requests.Add((req.SystemPrompt, req.Messages.ToList()));
                return call == 1 ? ToolCall("loop_probe") : Plain();
            });

        var executor = await CreateExecutorAsync(
            llm.Object,
            new TodoList
            {
                SessionId = "s1",
                Items = [new TodoItem { Id = "p", Content = "wait for user", Status = TodoStatus.Paused }]
            },
            manager => manager.RegisterToolAsync(new NoopTool()));

        await RunAsync(executor, "s1", withTodoWrite: true, maxSteps: 4, systemPrompt: "BASE PROMPT");

        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(r => r.SystemPrompt == "BASE PROMPT");
        CountKind(requests[0].Messages, "todo_paused").Should().Be(1);
        CountKind(requests[1].Messages, "todo_paused").Should().Be(1);
        requests[0].Messages[^1].Content.Should().Contain("kind=\"todo_paused\"");
        requests[0].Messages[^1].Content.Should().Contain("wait for user");
    }

    [Fact]
    public async Task EmptyTodos_AfterSeveralSteps_ShouldNotInjectTodoEmpty()
    {
        var requests = new List<IReadOnlyList<ChatMessage>>();
        var call = 0;
        var llm = Llm(requests, _ =>
        {
            call++;
            return call < 4 ? ToolCall("loop_probe") : Plain();
        });

        var executor = await CreateExecutorAsync(
            llm.Object,
            new TodoList { SessionId = "s1", Items = new() },
            manager => manager.RegisterToolAsync(new NoopTool()));

        await RunAsync(executor, "s1", withTodoWrite: true, maxSteps: 6);

        requests.Should().HaveCount(4);
        requests.SelectMany(r => r).Should().NotContain(m =>
            m.Content != null && m.Content.Contains("todo_empty"));
    }

    private static int CountKind(IReadOnlyList<ChatMessage> messages, string kind) =>
        messages.Count(m => m.Content != null && m.Content.Contains($"kind=\"{kind}\""));

    private static Mock<ILlmService> Llm(
        List<IReadOnlyList<ChatMessage>> requests,
        Func<int, IAsyncEnumerable<StreamUpdate>> next)
    {
        var call = 0;
        var llm = new Mock<ILlmService>();
        llm.Setup(s => s.CompleteStreamAsync(
                It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ChatRequest req, string? _, CancellationToken _) =>
            {
                call++;
                requests.Add(req.Messages.ToList());
                return next(call);
            });
        return llm;
    }

    private static async Task<List<IMessageEvent>> RunAsync(
        AgentExecutor executor,
        string sessionId,
        bool withTodoWrite,
        int maxSteps,
        string? systemPrompt = null)
    {
        var agent = new AgentDefinition
        {
            Name = "todo-policy",
            Runtime = AgentRuntime.Native,
            Mode = AgentMode.All,
            MaxSteps = maxSteps,
            SystemPrompt = systemPrompt
        };
        var context = new AgentContext
        {
            SessionId = sessionId,
            WorkingDirectory = "workspace-root",
            WorkspaceRoot = "workspace-root",
            ToolSettlement = withTodoWrite
                ? new Seeing.Agent.Abstractions.Execution.SessionToolSchemaResolution(
                    [new Seeing.Agent.Abstractions.Tools.ExposedTool(
                        "todowrite", "", "todowrite()",
                        new FunctionToolSchema { Function = new FunctionSchema { Name = "todowrite" } },
                        1, Seeing.Agent.Abstractions.Tools.ToolExposureMode.Inline)],
                    [],
                    Seeing.Agent.Abstractions.Tools.ToolExposureLayout.Flat,
                    null, 1, 0)
                : null
        };
        var messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "go" } };

        var events = new List<IMessageEvent>();
        await foreach (var evt in executor.ExecuteAsync(agent, messages, context, TestContext.Current.CancellationToken))
            events.Add(evt);
        return events;
    }

    private static TodoList Pending(string sessionId) => new()
    {
        SessionId = sessionId,
        Items = [new TodoItem { Id = "1", Content = "finish", Status = TodoStatus.Pending }]
    };

    private static async Task<AgentExecutor> CreateExecutorAsync(
        ILlmService llm,
        TodoList todos,
        Func<ToolManager, Task>? configureTools = null)
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
        if (configureTools is not null)
            await configureTools(toolManager);

        var todoStore = new Mock<ITodoStore>();
        todoStore.Setup(t => t.LoadAsync(It.IsAny<string>()))
            .ReturnsAsync(todos);

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
            NullLogger<AgentExecutor>.Instance,
            sessionManager: null,
            todoStore: todoStore.Object);
    }

    private static async IAsyncEnumerable<StreamUpdate> Plain()
    {
        yield return new StreamUpdate { IsComplete = true, FinishReason = "stop", ContentDelta = "done" };
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StreamUpdate> ToolCall(string name)
    {
        yield return new StreamUpdate
        {
            IsComplete = true,
            FinishReason = "tool_calls",
            ToolCallDeltas =
            [
                new ToolCall
                {
                    Id = "c1",
                    Type = "function",
                    Function = new FunctionCall { Name = name, Arguments = "{}" }
                }
            ]
        };
        await Task.CompletedTask;
    }

    private sealed class NoopTool : ITool
    {
        public string Id => "loop_probe";
        public string Description => "noop";
        public IReadOnlyList<string> Tags => [];
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(new ToolResult { Success = true, Output = "ok" });
    }

    private sealed class EndTurnTool : ITool
    {
        private readonly Func<string?> _reason;

        public EndTurnTool(Func<string?> reason) => _reason = reason;

        public string Id => "end_turn_tool";
        public string Description => "end the current turn";
        public IReadOnlyList<string> Tags => [];
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(new ToolResult
            {
                Success = true,
                Output = "done",
                TurnDirective = ToolTurnDirective.EndTurn,
                TurnDirectiveReason = _reason()
            });
    }
}
