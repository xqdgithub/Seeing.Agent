using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Llm;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Tests.Services;

public sealed class TuiCommandRouterTests
{
    [Theory]
    [InlineData("/help", true)]
    [InlineData("/h", true)]
    [InlineData("/?", true)]
    [InlineData("/new", true)]
    [InlineData("/NEW", true)]
    [InlineData("/sessions", true)]
    [InlineData("/resume", true)]
    [InlineData("/auto-approve", true)]
    [InlineData("/clear", false)]
    [InlineData("/compact", false)]
    [InlineData("/tools", false)]
    [InlineData("/mcp", false)]
    [InlineData("/unknown", false)]
    [InlineData("plain text", false)]
    public void IsLocal_ShouldClassifyCommand(string input, bool expected)
        => new TuiCommandRouter().IsLocal(input).Should().Be(expected);

    [Theory]
    [InlineData("/help")]
    [InlineData("/h")]
    [InlineData("/?")]
    public async Task ExecuteAsync_HelpAliases_ShouldReturnLocalOutput(string input)
    {
        var result = await new TuiCommandRouter().ExecuteAsync(input, Context(), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeFalse();
        result.Output.Should().NotBeNull();
        result.ForwardText.Should().BeNull();
    }

    [Theory]
    [InlineData("/exit")]
    [InlineData("/quit")]
    [InlineData("/q")]
    [InlineData("/Exit")]
    public async Task ExecuteAsync_ExitAliases_ShouldRequestExit(string input)
    {
        var result = await new TuiCommandRouter().ExecuteAsync(input, Context(), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeTrue();
    }

    [Theory]
    [InlineData("/clear")]
    [InlineData("/compact")]
    [InlineData("/tools")]
    [InlineData("/mcp")]
    [InlineData("/unknown")]
    [InlineData("/unknown with args")]
    [InlineData("/skill foo")]
    public async Task ExecuteAsync_NonLocalCommands_ShouldForwardOriginalText(string input)
    {
        var result = await new TuiCommandRouter().ExecuteAsync(input, Context(), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeFalse();
        result.Output.Should().BeNull();
        result.ForwardText.Should().Be(input);
    }

    [Fact]
    public async Task ExecuteAsync_PlainText_ShouldNotHandle()
    {
        var result = await new TuiCommandRouter().ExecuteAsync("hello", Context(), TestContext.Current.CancellationToken);

        result.Handled.Should().BeFalse();
        result.ForwardText.Should().Be("hello");
    }

    [Fact]
    public async Task ExecuteAsync_LocalCommands_ShouldBeCaseInsensitive()
    {
        var (controller, orchestrator, _, _) = NewController();

        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);
        var result = await new TuiCommandRouter().ExecuteAsync("/RENAME Hello", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.ForwardText.Should().BeNull();
        orchestrator.Verify(o => o.RenameSessionAsync("ses_x", "Hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NewWithTitle_ShouldCreateSession_AndNotForward()
    {
        var (controller, orchestrator, _, _) = NewController();

        var result = await new TuiCommandRouter().ExecuteAsync("/new My Title", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.ForwardText.Should().BeNull();
        result.Output.Should().NotBeNull();
        orchestrator.Verify(
            o => o.CreateSessionAsync("My Title", null, null, It.IsAny<CancellationToken>()),
            Times.Once);
        controller.Current.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RenameWithoutTitle_ShouldNotCallOrchestrator()
    {
        var (controller, orchestrator, _, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var result = await new TuiCommandRouter().ExecuteAsync("/rename", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
        orchestrator.Verify(
            o => o.RenameSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AutoApprove_ShouldParseMode()
    {
        var (controller, _, sessionManager, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);
        var router = new TuiCommandRouter();

        await router.ExecuteAsync("/auto-approve on", Context(controller), TestContext.Current.CancellationToken);
        sessionManager.Verify(
            m => m.SetAutoApproveAsync("ses_x", SessionAutoApprove.Enabled, It.IsAny<CancellationToken>()),
            Times.Once);

        var invalid = await router.ExecuteAsync("/auto-approve bogus", Context(controller), TestContext.Current.CancellationToken);
        invalid.Output.Should().NotBeNull();
        sessionManager.Verify(
            m => m.SetAutoApproveAsync(It.IsAny<string>(), It.IsAny<SessionAutoApprove>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ThinkingWithArgument_ShouldPassLevel()
    {
        var (controller, _, sessionManager, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var result = await new TuiCommandRouter().ExecuteAsync("/thinking high", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.ToggleReasoning.Should().BeFalse();
        sessionManager.Verify(
            m => m.SetThinkingEffortAsync("ses_x", "high", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ReasoningNoArg_ShouldToggleAndSignalEngine()
    {
        var router = new TuiCommandRouter();

        var result = await router.ExecuteAsync("/reasoning", Context(), TestContext.Current.CancellationToken);

        result.ToggleReasoning.Should().BeTrue();
        result.ForwardText.Should().BeNull();
        router.ReasoningEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ReasoningExplicit_ShouldSetAndSignalEngine()
    {
        var router = new TuiCommandRouter();

        var on = await router.ExecuteAsync("/reasoning on", Context(), TestContext.Current.CancellationToken);
        on.ToggleReasoning.Should().BeTrue();
        router.ReasoningEnabled.Should().BeTrue();

        var off = await router.ExecuteAsync("/reasoning off", Context(), TestContext.Current.CancellationToken);
        off.ToggleReasoning.Should().BeTrue();
        router.ReasoningEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ReasoningInvalid_ShouldReturnUsageWithoutSignal()
    {
        var router = new TuiCommandRouter();

        var result = await router.ExecuteAsync("/reasoning maybe", Context(), TestContext.Current.CancellationToken);

        result.ToggleReasoning.Should().BeFalse();
        result.Output.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ModelNoArg_WhenSelectionCancelled_ShouldNotChangeModel()
    {
        var (controller, _, sessionManager, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModelsByType(It.IsAny<ModelType>(), It.IsAny<string>()))
            .Returns(new Dictionary<string, ModelConfig>
            {
                ["openai/gpt-4o"] = new() { Id = "gpt-4o", Name = "GPT-4o", Provider = "openai" },
            });
        // Esc 取消：选择器返回哨兵值。
        var surface = new ScriptedSurface().Enqueue(TuiCommandRouter.CancelSentinel);

        var result = await new TuiCommandRouter().ExecuteAsync(
            "/model",
            Context(controller, surface: surface, models: models.Object),
            TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        surface.PromptCount.Should().Be(1);
        controller.Current!.ModelId.Should().NotBe("openai/gpt-4o");
        sessionManager.Verify(
            m => m.SetModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ResumeWithArgument_ShouldSwitchActiveSession()
    {
        var (controller, orchestrator, _, _) = NewController();
        var result = await new TuiCommandRouter().ExecuteAsync("/resume ses_x", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        controller.Current!.SessionId.Should().Be("ses_x");
        orchestrator.Verify(o => o.GetSessionAsync("ses_x", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SessionsNoArg_ShouldPromptAndSwitch()
    {
        var (controller, orchestrator, _, _) = NewController();
        orchestrator.Setup(o => o.ListSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionData>
            {
                new() { Id = "ses_a", Title = "A", UpdatedAt = DateTime.Now.AddMinutes(-5) },
                new() { Id = "ses_b", Title = "[B]", UpdatedAt = DateTime.Now },
            });
        var surface = new ScriptedSurface().Enqueue("ses_b");

        var result = await new TuiCommandRouter().ExecuteAsync("/sessions", Context(controller, surface: surface), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
        surface.PromptCount.Should().Be(1);
        controller.Current!.SessionId.Should().Be("ses_b");
    }

    [Fact]
    public async Task ExecuteAsync_SessionsNoArgEmpty_ShouldNotPrompt()
    {
        var (controller, orchestrator, _, _) = NewController();
        orchestrator.Setup(o => o.ListSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionData>());
        var surface = new ScriptedSurface();

        var result = await new TuiCommandRouter().ExecuteAsync("/sessions", Context(controller, surface: surface), TestContext.Current.CancellationToken);

        result.Output.Should().NotBeNull();
        surface.PromptCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ResumeNoArg_ShouldPromptAndSwitch()
    {
        var (controller, orchestrator, _, _) = NewController();
        orchestrator.Setup(o => o.ListSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionData>
            {
                new() { Id = "ses_a", Title = "A", UpdatedAt = DateTime.Now },
            });
        var surface = new ScriptedSurface().Enqueue("ses_a");

        var result = await new TuiCommandRouter().ExecuteAsync("/resume", Context(controller, surface: surface), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        surface.PromptCount.Should().Be(1);
        controller.Current!.SessionId.Should().Be("ses_a");
    }

    [Fact]
    public async Task ExecuteAsync_AgentNoArg_ShouldPromptAndSet()
    {
        var (controller, _, _, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.GetPrimaryAgentsAsync()).ReturnsAsync(new List<AgentDefinition>
        {
            new() { Name = "build", Description = "默认" },
            new() { Name = "plan", Description = "计划" },
        });
        var surface = new ScriptedSurface().Enqueue("plan");

        var result = await new TuiCommandRouter().ExecuteAsync(
            "/agent",
            Context(controller, surface: surface, agents: agents.Object),
            TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        surface.PromptCount.Should().Be(1);
        controller.Current!.AgentId.Should().Be("plan");
    }

    [Fact]
    public async Task ExecuteAsync_ModelNoArg_ShouldPromptAndSet()
    {
        var (controller, _, sessionManager, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModelsByType(It.IsAny<ModelType>(), It.IsAny<string>()))
            .Returns(new Dictionary<string, ModelConfig>
            {
                ["openai/gpt-4o"] = new() { Id = "gpt-4o", Name = "GPT-4o", Provider = "openai" },
            });
        var surface = new ScriptedSurface().Enqueue("openai/gpt-4o");

        var result = await new TuiCommandRouter().ExecuteAsync(
            "/model",
            Context(controller, surface: surface, models: models.Object),
            TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        surface.PromptCount.Should().Be(1);
        controller.Current!.ModelId.Should().Be("openai/gpt-4o");
        sessionManager.Verify(
            m => m.SetModelAsync("ses_x", "openai/gpt-4o", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ScenarioNoArg_ShouldPromptAndSet()
    {
        var (controller, _, _, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var scenarios = new Mock<IScenarioCatalog>();
        scenarios.Setup(s => s.ListAll()).Returns(new List<ScenarioDefinition>
        {
            new("code", [], "build", new Dictionary<string, string>(), []),
            new("research", [], "explore", new Dictionary<string, string>(), []),
        });
        var surface = new ScriptedSurface().Enqueue("research");

        var result = await new TuiCommandRouter().ExecuteAsync(
            "/scenario",
            Context(controller, surface: surface, scenarios: scenarios.Object),
            TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        surface.PromptCount.Should().Be(1);
        controller.Current!.Scenario.Should().Be("research");
    }

    [Fact]
    public async Task ExecuteAsync_ThinkingNoArg_ShouldPromptFromModelLevels()
    {
        var (controller, _, _, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GetModelConfig("m1")).Returns(new ModelConfig
        {
            Id = "m1",
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions
                {
                    Supported = true,
                    Levels =
                    [
                        new ThinkingLevel { Key = "low", Label = "低" },
                        new ThinkingLevel { Key = "high", Label = "高" },
                    ],
                },
            },
        });
        var state = new TuiViewState { SessionId = "ses_x", ModelId = "m1" };
        var surface = new ScriptedSurface().Enqueue("high");

        var result = await new TuiCommandRouter().ExecuteAsync(
            "/thinking",
            Context(controller, state: state, surface: surface, llm: llm.Object),
            TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        surface.PromptCount.Should().Be(1);
        controller.Current!.ThinkingEffort.Should().Be("high");
        llm.Verify(l => l.GetModelConfig("m1"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ThinkingNoArgWithoutLevels_ShouldReturnHint()
    {
        var (controller, _, _, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GetModelConfig("m1")).Returns(new ModelConfig { Id = "m1" });
        var state = new TuiViewState { SessionId = "ses_x", ModelId = "m1" };
        var surface = new ScriptedSurface();

        var result = await new TuiCommandRouter().ExecuteAsync(
            "/thinking",
            Context(controller, state: state, surface: surface, llm: llm.Object),
            TestContext.Current.CancellationToken);

        result.Output.Should().NotBeNull();
        surface.PromptCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_TodoEmpty_ShouldShowHint()
    {
        var result = await new TuiCommandRouter().ExecuteAsync(
            "/todo",
            Context(state: new TuiViewState { SessionId = "ses_x" }),
            TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TodoNonEmpty_ShouldRenderPanel()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_x",
            Todos = [new TuiTodo("写测试", "in_progress", null)],
        };

        var result = await new TuiCommandRouter().ExecuteAsync("/todo", Context(state: state), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ExpandHit_ShouldMarkExpandedAndRenderOutput()
    {
        var state = new TuiViewState { SessionId = "ses_x" };
        var tool = new TuiToolState
        {
            CallId = "call_1",
            Name = "bash",
            Status = TuiToolStatus.Success,
            Output = "line1\nline2",
        };
        state.Upsert(new TuiBlock { Key = "tool:call_1", Kind = TuiBlockKind.Tool, Tool = tool });

        var result = await new TuiCommandRouter().ExecuteAsync("/expand call_1", Context(state: state), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
        tool.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ExpandUnknownCall_ShouldReportMissing()
    {
        var state = new TuiViewState { SessionId = "ses_x" };

        var result = await new TuiCommandRouter().ExecuteAsync("/expand nope", Context(state: state), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_CancelAll_ShouldCallCascade()
    {
        var (controller, _, _, submitter) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        await new TuiCommandRouter().ExecuteAsync("/cancel all", Context(controller), TestContext.Current.CancellationToken);

        submitter.Verify(
            s => s.CancelBySessionAsync("ses_x", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Open_ShouldSwitchActiveSession()
    {
        var (controller, _, _, _) = NewController();

        var result = await new TuiCommandRouter().ExecuteAsync("/open ses_x", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        controller.Current!.SessionId.Should().Be("ses_x");
    }

    [Fact]
    public async Task ExecuteAsync_AutoApproveWithoutArgs_ShouldCycleTriState()
    {
        var (controller, _, sessionManager, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);
        controller.Current!.AutoApprove = SessionAutoApprove.FollowGlobal;

        var result = await new TuiCommandRouter().ExecuteAsync("/auto-approve", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        sessionManager.Verify(
            m => m.SetAutoApproveAsync("ses_x", SessionAutoApprove.Enabled, It.IsAny<CancellationToken>()),
            Times.Once);
        controller.Current!.AutoApprove.Should().Be(SessionAutoApprove.Enabled);
    }

    [Theory]
    [InlineData("on", SessionAutoApprove.Enabled)]
    [InlineData("off", SessionAutoApprove.Disabled)]
    [InlineData("follow", SessionAutoApprove.FollowGlobal)]
    public async Task ExecuteAsync_AutoApproveWithValue_ShouldSetMode(string args, SessionAutoApprove expected)
    {
        var (controller, _, sessionManager, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var result = await new TuiCommandRouter().ExecuteAsync($"/auto-approve {args}", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        sessionManager.Verify(
            m => m.SetAutoApproveAsync("ses_x", expected, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AutoApproveWithBadValue_ShouldReturnUsage()
    {
        var (controller, _, sessionManager, _) = NewController();
        await controller.SwitchAsync("ses_x", TestContext.Current.CancellationToken);

        var result = await new TuiCommandRouter().ExecuteAsync("/auto-approve maybe", Context(controller), TestContext.Current.CancellationToken);

        result.Handled.Should().BeTrue();
        result.Output.Should().NotBeNull();
        sessionManager.Verify(
            m => m.SetAutoApproveAsync(It.IsAny<string>(), It.IsAny<SessionAutoApprove>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static TuiCommandContext Context(
        TuiSessionController? controller = null,
        TuiViewState? state = null,
        ITerminalSurface? surface = null,
        IAgentRegistry? agents = null,
        IModelConfigManager? models = null,
        ILlmService? llm = null,
        IScenarioCatalog? scenarios = null)
        => new(
            controller ?? NewController().Controller,
            null!,
            null!,
            surface ?? new ScriptedSurface(),
            state,
            agents ?? new Mock<IAgentRegistry>().Object,
            models ?? new Mock<IModelConfigManager>().Object,
            llm ?? new Mock<ILlmService>().Object,
            scenarios ?? new Mock<IScenarioCatalog>().Object);

    private static (TuiSessionController Controller, Mock<IChatOrchestrator> Orchestrator, Mock<ISessionManager> SessionManager, Mock<IExecutionSubmitter> Submitter) NewController()
    {
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator
            .Setup(o => o.CreateSessionAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SessionData.Create());
        orchestrator
            .Setup(o => o.GetSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var session = SessionData.Create();
                session.Id = id;
                return session;
            });
        orchestrator
            .Setup(o => o.ListSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionData>());

        var submitter = new Mock<IExecutionSubmitter>();
        submitter
            .Setup(s => s.CancelBySessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sessionManager = new Mock<ISessionManager>();
        var groupManager = new Mock<ISessionGroupManager>();

        var controller = new TuiSessionController(
            orchestrator.Object,
            submitter.Object,
            sessionManager.Object,
            groupManager.Object);

        return (controller, orchestrator, sessionManager, submitter);
    }

    /// <summary>按脚本返回选择结果的终端出口替身，避免真实交互提示。</summary>
    private sealed class ScriptedSurface : ITerminalSurface
    {
        private readonly Queue<object?> _answers = new();

        public int PromptCount { get; private set; }

        public ScriptedSurface Enqueue(object? answer)
        {
            _answers.Enqueue(answer);
            return this;
        }

        public Task UpdateAsync(IRenderable view, TuiCaret? caret = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task CommitAsync(IRenderable committed, CancellationToken ct = default) => Task.CompletedTask;

        public Task<T> PromptAsync<T>(Func<IAnsiConsole, CancellationToken, Task<T>> prompt, CancellationToken ct = default)
        {
            PromptCount++;
            if (_answers.Count == 0)
                throw new InvalidOperationException("测试未提供交互选择结果");

            var answer = _answers.Dequeue();
            return Task.FromResult(answer is null ? default! : (T)answer);
        }

        public IAnsiConsole Console { get; } = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(new StringWriter()),
        });

        public Task StopAsync() => Task.CompletedTask;
    }
}
