using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 命令面板分组契约：分组判定与组内排序在 <see cref="CommandListService"/> 一处决定，
/// 组件只按返回顺序渲染组标题。
/// </summary>
public class CommandListServiceTests
{
    [Fact]
    public void GetAvailableCommands_Should_Order_By_Group_Then_SortOrder_Then_Name()
    {
        var service = CreateService(
            Command("compact", category: CommandCategory.Basic, sortOrder: 50),
            Command("help", category: CommandCategory.Basic, sortOrder: 10),
            Skill("brainstorming", sortOrder: 50),
            Skill("systematic-debugging", sortOrder: 50),
            Markdown("deploy", "C:/ws/.seeing/commands/deploy.md"),
            Extension("mcp__docs__search"));

        var commands = service.GetAvailableCommands();

        commands.Select(c => c.Name).Should().Equal(
            "help", "compact",              // 内置：SortOrder → Name
            "brainstorming", "systematic-debugging", // 技能
            "deploy",                       // 自定义
            "mcp__docs__search");           // 扩展
    }

    [Fact]
    public void GetAvailableCommands_Should_Fill_Group_And_Flags()
    {
        var service = CreateService(
            Command("help", category: CommandCategory.Basic),
            Skill("brainstorming"),
            Markdown("deploy", "C:/ws/.seeing/commands/deploy.md"),
            Extension("mcp__docs__search"));

        var commands = service.GetAvailableCommands().ToDictionary(c => c.Name);

        commands["help"].Group.Should().Be(CommandGroupKind.BuiltIn);
        commands["help"].GroupTitle.Should().Be("内置命令");
        commands["help"].IsBuiltIn.Should().BeTrue();
        commands["help"].Category.Should().Be(nameof(CommandCategory.Basic), "内置组内仍显示 Category 徽标");

        commands["brainstorming"].Group.Should().Be(CommandGroupKind.Skill);
        commands["brainstorming"].GroupTitle.Should().Be("技能");
        commands["brainstorming"].IsSkill.Should().BeTrue("Type=Skill 必须映射到视图模型，否则面板区分不出技能");
        commands["brainstorming"].IsBuiltIn.Should().BeFalse();

        commands["deploy"].Group.Should().Be(CommandGroupKind.Custom);
        commands["deploy"].IsCustom.Should().BeTrue();

        commands["mcp__docs__search"].Group.Should().Be(CommandGroupKind.Extension);
    }

    [Fact]
    public void GetAvailableCommands_Should_Exclude_Hidden_Commands()
    {
        var hidden = Command("secret", category: CommandCategory.System);
        hidden = WithMetadata(hidden, m => new CommandMetadata
        {
            Name = m.Name,
            Description = m.Description,
            Category = m.Category,
            IsHidden = true
        });

        var service = CreateService(Command("help", category: CommandCategory.Basic), hidden);

        service.GetAvailableCommands().Select(c => c.Name).Should().Equal("help");
    }

    [Fact]
    public void GetAvailableCommands_Should_Mark_Session_Required_Commands_Disabled_Without_Session()
    {
        var service = CreateService(Command("compact", category: CommandCategory.Basic));

        var command = service.GetAvailableCommands().Single();

        command.IsDisabled.Should().BeTrue("compact 需要活动会话");
        command.DisabledReason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// 同名技能同时登记在 Native / ACP 两个 Runtime：面板必须按「当前选中 Agent 的 Runtime」取其中一组。
    /// </summary>
    [Fact]
    public void GetAvailableCommands_Should_Follow_Selected_Agent_Runtime()
    {
        var native = Skill("brainstorming", description: "Native 版", runtime: AgentRuntime.Native);
        var acp = Skill("brainstorming", description: "ACP 版", runtime: AgentRuntime.AcpPassthrough);

        var agents = new Mock<IAgentRegistry>();
        agents.Setup(r => r.GetAgentAsync("build"))
            .ReturnsAsync(new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native });
        agents.Setup(r => r.GetAgentAsync("cursor"))
            .ReturnsAsync(new AgentDefinition { Name = "cursor", Runtime = AgentRuntime.AcpPassthrough });

        var service = CreateService(agents.Object, native, acp);

        var nativeView = service.GetAvailableCommands(CreateSession("build"));
        nativeView.Should().ContainSingle(c => c.Name == "brainstorming")
            .Which.Description.Should().Be("Native 版", "Native 智能体下只能用 Native 那一组命令");

        var acpView = service.GetAvailableCommands(CreateSession("cursor"));
        acpView.Should().ContainSingle(c => c.Name == "brainstorming")
            .Which.Description.Should().Be("ACP 版", "切到 ACP 智能体后同名的透传命令接管");
    }

    private static SessionState CreateSession(string agent)
        => new() { CurrentSession = new SessionData { SelectedAgent = agent } };

    private static CommandListService CreateService(params ICommand[] commands)
        => CreateService(new Mock<IAgentRegistry>().Object, commands);

    private static CommandListService CreateService(IAgentRegistry agents, params ICommand[] commands)
        => new(new FakeCommandRegistry(commands), agents);

    private static ICommand Command(string name, CommandCategory category, int sortOrder = 100)
        => new FakeCommand(new CommandMetadata
        {
            Name = name,
            Description = $"{name} 命令",
            Category = category,
            SortOrder = sortOrder
        });

    private static ICommand Skill(
        string name,
        string? description = null,
        int sortOrder = 100,
        AgentRuntime runtime = AgentRuntime.Native)
        => new FakeCommand(new CommandMetadata
        {
            Name = name,
            Description = description ?? $"加载技能：{name}",
            Category = CommandCategory.Tools,
            Type = CommandType.Skill,
            SortOrder = sortOrder,
            SupportedRuntimes = [runtime]
        });

    private static ICommand Markdown(string name, string source)
        => new FakeCommand(new CommandMetadata
        {
            Name = name,
            Description = $"{name} 命令",
            Category = CommandCategory.Other,
            Source = source
        });

    private static ICommand Extension(string name)
        => new FakeCommand(new CommandMetadata
        {
            Name = name,
            Description = $"{name} 命令",
            Category = CommandCategory.Tools,
            Source = "mcp"
        });

    private static ICommand WithMetadata(ICommand command, Func<CommandMetadata, CommandMetadata> map)
        => new FakeCommand(map(command.Metadata));

    private sealed class FakeCommand(CommandMetadata metadata) : ICommand
    {
        public CommandMetadata Metadata { get; } = metadata;

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok());
    }

    private sealed class FakeCommandRegistry : ICommandRegistry
    {
        private readonly ICommand[] _commands;

        public FakeCommandRegistry(ICommand[] commands) => _commands = commands;

        public int Count => _commands.Length;

        public void Register(ICommand command) { }

        public void RegisterAll(IEnumerable<ICommand> commands) { }

        public ICommand? GetCommand(string name) => null;

        public ICommand? GetCommand(string name, AgentRuntime runtime) => null;

        public IEnumerable<ICommand> GetAllCommands() => _commands;

        public IEnumerable<ICommand> GetCommandsByRuntime(AgentRuntime runtime)
            => _commands.Where(c => c.Metadata.SupportsRuntime(runtime));

        public IEnumerable<ICommand> GetCommandsByCategory(CommandCategory category)
            => _commands.Where(c => c.Metadata.Category == category);

        public IEnumerable<CommandMetadata> GetAllMetadata() => _commands.Select(c => c.Metadata);

        public bool HasCommand(string name) => false;

        public bool Unregister(string name) => false;

        public bool Unregister(ICommand command) => false;
    }
}
