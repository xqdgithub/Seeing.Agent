using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Mcp.Commands;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.Commands;

public class McpPromptCommandTests
{
    [Fact]
    public async Task Execute_ShouldContinue_False_And_DeliverAsync_Once_Not_AddMessage()
    {
        var promptSession = new FakePromptSession(
            new McpPromptGetResult(
            [
                new McpPromptMessage("user", "hello from prompt"),
                new McpPromptMessage("assistant", "assistant hint"),
            ]));

        var runtime = new Mock<ISessionRuntime>(MockBehavior.Strict);
        InboxMessage? delivered = null;
        runtime
            .Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((msg, _) => delivered = msg)
            .ReturnsAsync(InboxResult.Ok("mid-1"));

        var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);

        var services = new ServiceCollection()
            .AddSingleton(runtime.Object)
            .AddSingleton(sessionManager.Object)
            .BuildServiceProvider();

        var command = new McpPromptCommand(
            serverName: "demo",
            prompt: new McpPromptInfo("greet", "A greeting"),
            resolveSession: _ => promptSession);

        var result = await command.ExecuteAsync(new CommandContext
        {
            CommandName = command.Metadata.Name,
            Input = $"/{command.Metadata.Name}",
            Arguments = "",
            SessionId = "sess-1",
            Services = services,
        });

        result.Success.Should().BeTrue();
        result.ShouldContinue.Should().BeFalse();
        result.RemoveCommandMessage.Should().BeTrue();

        runtime.Verify(
            r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
        sessionManager.Verify(
            m => m.AddMessageAsync(It.IsAny<string>(), It.IsAny<SessionMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        delivered.Should().NotBeNull();
        delivered!.TargetSessionId.Should().Be("sess-1");
        delivered.Delivery.Should().Be(InboxDelivery.Wake);
        delivered.Presentation.Should().Be(InboxPresentation.User);
        delivered.Sender.Kind.Should().Be(InboxSenderKind.Human);
        delivered.Body.Should().Contain("hello from prompt");
        delivered.Body.Should().Contain("assistant");
        delivered.Body.Should().Contain("assistant hint");
    }

    [Fact]
    public void BuildName_Sanitizes_NonSafe_Characters()
    {
        McpPromptCommandNames.Build("my server!", "prompt/one")
            .Should().Be("mcp-prompt-my_server_-prompt_one");
    }

    [Fact]
    public void Sync_Skips_When_HasCommand()
    {
        var registry = new RecordingCommandRegistry();
        registry.Seed("mcp-prompt-demo-greet");

        var caps = new McpServerCapabilitiesInfo(
            true, false, false, false, true, false, false, false);
        var status = new McpServerStatus(
            "demo", McpConnectionState.Connected, config: null, McpServerPriority.Normal,
            toolCount: 0, toolNames: Array.Empty<string>(),
            createdAt: DateTimeOffset.UtcNow,
            lastConnectedAt: DateTimeOffset.UtcNow, lastErrorAt: null, lastError: null,
            reconnectAttempts: 0, activePolicy: null,
            capabilities: caps, resourceCount: null, promptCount: 1, skillCount: null);

        var promptSession = new FakePromptSession(
            new McpPromptGetResult([]),
            listResult: new McpPromptListResult([new McpPromptInfo("greet", "A greeting")]));

        var manager = new Mock<IMcpManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetAllStatus())
            .Returns(new Dictionary<string, McpServerStatus> { ["demo"] = status });
        manager.SetupAdd(m => m.StatusChanged += It.IsAny<EventHandler<McpStatusChangedEventArgs>>());
        manager.SetupRemove(m => m.StatusChanged -= It.IsAny<EventHandler<McpStatusChangedEventArgs>>());

        using var sync = new McpPromptCommandSync(
            manager.Object,
            registry,
            resolveSession: _ => promptSession,
            NullLogger.Instance);
        sync.Start();

        registry.RegisterCalls.Should().BeEmpty(
            "HasCommand 为 true 时必须跳过，禁止靠 Register 覆盖");
    }

    private sealed class FakePromptSession : IMcpSession
    {
        private readonly McpPromptGetResult _getResult;
        private readonly McpPromptListResult _listResult;

        public FakePromptSession(
            McpPromptGetResult getResult,
            McpPromptListResult? listResult = null)
        {
            _getResult = getResult;
            _listResult = listResult ?? new McpPromptListResult([new McpPromptInfo("greet")]);
            Capabilities = new McpServerCapabilitiesInfo(
                true, false, false, false, true, false, false, false);
        }

        public string ServerName => "demo";
        public McpServerCapabilitiesInfo Capabilities { get; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpSessionToolInfo>>([]);

        public Task<McpSessionToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpSessionToolCallResult(false, ""));

        public Task<McpResourceListResult> ListResourcesAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
            => Task.FromResult(McpResourceListResult.Unsupported());

        public Task<McpResourceReadResult> ReadResourceAsync(
            string uri, CancellationToken cancellationToken = default)
            => Task.FromResult(McpResourceReadResult.Unsupported());

        public Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
            => Task.FromResult(McpResourceTemplateListResult.Unsupported());

        public Task<McpPromptListResult> ListPromptsAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_listResult);

        public Task<McpPromptGetResult> GetPromptAsync(
            string name,
            IReadOnlyDictionary<string, object?>? arguments = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_getResult);

        public Task<McpSkillExtListResult> ListSkillsAsync(
            string? cursor = null, CancellationToken cancellationToken = default)
            => Task.FromResult(McpSkillExtListResult.Unsupported());

        public Task<McpSkillExtGetResult> GetSkillAsync(
            string uri, CancellationToken cancellationToken = default)
            => Task.FromResult(McpSkillExtGetResult.Unsupported());
    }

    private sealed class RecordingCommandRegistry : ICommandRegistry
    {
        private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RegisterCalls { get; } = [];

        public int Count => _commands.Count;

        public void Seed(string name) => _commands[name] = new StubCommand(name);

        public void Register(ICommand command)
        {
            RegisterCalls.Add(command.Metadata.Name);
            _commands[command.Metadata.Name] = command;
        }

        public void RegisterAll(IEnumerable<ICommand> commands)
        {
            foreach (var c in commands)
                Register(c);
        }

        public bool Unregister(string name) => _commands.Remove(name);
        public bool HasCommand(string name) => _commands.ContainsKey(name);
        public ICommand? GetCommand(string name)
            => _commands.TryGetValue(name, out var c) ? c : null;
        public ICommand? GetCommand(string name, AgentRuntime runtime) => GetCommand(name);
        public IEnumerable<ICommand> GetAllCommands() => _commands.Values;
        public IEnumerable<ICommand> GetCommandsByRuntime(AgentRuntime runtime) => _commands.Values;
        public IEnumerable<ICommand> GetCommandsByCategory(CommandCategory category)
            => _commands.Values.Where(c => c.Metadata.Category == category);
        public IEnumerable<CommandMetadata> GetAllMetadata()
            => _commands.Values.Select(c => c.Metadata);

        private sealed class StubCommand(string name) : ICommand
        {
            public CommandMetadata Metadata { get; } = CommandMetadata.Simple(name, "stub");
            public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
                => Task.FromResult(CommandResult.Ok());
        }
    }
}
