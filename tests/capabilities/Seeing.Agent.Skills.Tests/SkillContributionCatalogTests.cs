using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Agent.Skills.Commands;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 外部技能贡献端口：并进快照、不盖本地、加载回源、命令重名跳过。
/// </summary>
public sealed class SkillContributionCatalogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-skill-contrib-{Guid.NewGuid():N}");

    private readonly string _workspace;
    private readonly string _userHome;

    public SkillContributionCatalogTests()
    {
        _workspace = Path.Combine(_root, "ws");
        _userHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_userHome);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Publish_Merges_Contribution_Alongside_Local_File()
    {
        var project = ProjectSkillRoot();
        WriteSkill(project, "demo", "demo", "本地文件技能");

        var registry = new SkillContributionRegistry();
        var catalog = CreateCatalog(registry, project);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        registry.Register(new FakeContributionSource(
            "mcp",
            [new SkillContributionDescriptor("mcp_s1_demo", "远端技能", "mcp:s1")],
            key => key == "mcp_s1_demo"
                ? new SkillContributionContent("远端正文", [])
                : null));

        await catalog.RefreshContributionsAsync(TestContext.Current.CancellationToken);

        catalog.Find("demo")!.Source.Should().Be(SkillSource.ProjectFile);
        var contrib = catalog.Find("mcp_s1_demo");
        contrib.Should().NotBeNull();
        contrib!.Source.Should().Be(SkillSource.Contribution);
        contrib.ProviderId.Should().Be("mcp:s1");
        contrib.DirectoryPath.Should().BeEmpty();
        contrib.Info.Content.Should().BeEmpty("发现期只合并元数据");
        catalog.Current.Conflicts.Should().NotContain(c => c.Name == "mcp_s1_demo");
    }

    [Fact]
    public async Task Contribution_Conflicting_With_File_Is_Skipped_File_Wins()
    {
        var project = ProjectSkillRoot();
        WriteSkill(project, "demo", "demo", "本地文件技能");

        var registry = new SkillContributionRegistry();
        var catalog = CreateCatalog(registry, project);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        registry.Register(new FakeContributionSource(
            "mcp",
            [new SkillContributionDescriptor("demo", "冲突贡献", "mcp:s1")],
            _ => new SkillContributionContent("不应出现", [])));

        await catalog.RefreshContributionsAsync(TestContext.Current.CancellationToken);

        var entry = catalog.Find("demo");
        entry.Should().NotBeNull();
        entry!.Source.Should().Be(SkillSource.ProjectFile);
        entry.Info.Description.Should().Be("本地文件技能");

        catalog.Current.Conflicts.Should().Contain(c =>
            c.Name == "demo"
            && c.Winner == SkillSource.ProjectFile
            && c.Loser == SkillSource.Contribution);
    }

    [Fact]
    public async Task Contribution_Does_Not_Call_RegisterAsync()
    {
        var registry = new SkillContributionRegistry();
        var catalog = CreateCatalog(registry);
        registry.Register(new FakeContributionSource(
            "mcp",
            [new SkillContributionDescriptor("mcp_s1_x", "x", "mcp:s1")],
            _ => null));

        await catalog.RefreshContributionsAsync(TestContext.Current.CancellationToken);

        catalog.Find("mcp_s1_x")!.Source.Should().Be(SkillSource.Contribution);
        catalog.Current.Entries.Should().NotContain(e => e.Source == SkillSource.Registered);
    }

    [Fact]
    public async Task Load_Contribution_Callbacks_Source_LoadAsync()
    {
        var registry = new SkillContributionRegistry();
        var catalog = CreateCatalog(registry);
        var loadCount = 0;
        registry.Register(new FakeContributionSource(
            "mcp",
            [new SkillContributionDescriptor("mcp_s1_body", "描述", "mcp:s1")],
            key =>
            {
                Interlocked.Increment(ref loadCount);
                return key == "mcp_s1_body"
                    ? new SkillContributionContent("# Hello\nbody", [
                        new SkillContributionAttachment("refs/a.md", "skill://s1/a.md", "text/markdown")])
                    : null;
            }));

        await catalog.RefreshContributionsAsync(TestContext.Current.CancellationToken);

        var content = await registry.LoadAsync("mcp_s1_body", TestContext.Current.CancellationToken);
        content.Should().NotBeNull();
        content!.MarkdownBody.Should().Contain("Hello");
        content.Attachments.Should().ContainSingle(a => a.Uri == "skill://s1/a.md");
        loadCount.Should().Be(1);
    }

    [Fact]
    public async Task DynamicSkillCommandSync_Skips_When_HasCommand()
    {
        var registry = new SkillContributionRegistry();
        var catalog = CreateCatalog(registry);
        registry.Register(new FakeContributionSource(
            "mcp",
            [new SkillContributionDescriptor("builtin-taken", "冲突命令", "mcp:s1")],
            _ => null));
        await catalog.RefreshContributionsAsync(TestContext.Current.CancellationToken);

        var commandRegistry = new FakeCommandRegistry();
        commandRegistry.Register(new StubCommand("builtin-taken"));

        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton<ISkillCatalog>(catalog);
        services.AddSingleton<ICommandRegistry>(commandRegistry);
        services.AddSingleton(Mock.Of<ISessionManager>());

        await using var provider = services.BuildServiceProvider();
        var module = new SkillsModule();
        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            commandRegistry.GetCommand("builtin-taken").Should().BeOfType<StubCommand>(
                "与内置命令重名时必须跳过，禁止靠 Register 覆盖");
            commandRegistry.DynamicSkillNames.Should().NotContain("builtin-taken");
        }
        finally
        {
            await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);
        }
    }

    private SkillCatalogCore CreateCatalog(ISkillContributionRegistry registry, params string[] roots)
    {
        var catalog = new SkillCatalogCore(
            NullLogger<SkillCatalogCore>.Instance,
            new FakeDirectories(
                userSeeing: Path.Combine(_userHome, ".seeing"),
                projectSeeing: Path.Combine(_workspace, ".seeing")),
            writeGate: null,
            workspaces: null,
            contributions: registry);
        catalog.SetRoots(roots);
        return catalog;
    }

    private string ProjectSkillRoot() => Path.Combine(_workspace, ".seeing", "skills");

    private static void WriteSkill(string root, string directoryName, string name, string description)
    {
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"""
             ---
             name: {name}
             description: {description}
             ---

             正文 {name}
             """);
    }

    private sealed class FakeContributionSource(
        string sourceKind,
        IReadOnlyList<SkillContributionDescriptor> descriptors,
        Func<string, SkillContributionContent?> load) : ISkillContributionSource
    {
        public string SourceKind { get; } = sourceKind;

        public Task<IReadOnlyList<SkillContributionDescriptor>> ListAsync(
            CancellationToken ct = default)
            => Task.FromResult(descriptors);

        public Task<SkillContributionContent?> LoadAsync(
            string key,
            CancellationToken ct = default)
            => Task.FromResult(load(key));
    }

    private sealed class FakeDirectories(string userSeeing, string projectSeeing) : ISeeingDirectories
    {
        public string UserSeeingDirectory { get; } = userSeeing;
        public string ProjectSeeingDirectory { get; } = projectSeeing;
    }

    private sealed class StubCommand(string name) : ICommand
    {
        public CommandMetadata Metadata { get; } = new()
        {
            Name = name,
            Description = "stub",
            Usage = $"/{name}",
            SupportedRuntimes = [AgentRuntime.Native],
        };

        public Task<CommandResult> ExecuteAsync(
            CommandContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok("ok"));
    }

    private sealed class FakeCommandRegistry : ICommandRegistry
    {
        private readonly Dictionary<string, ICommand> _commands =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> DynamicSkillNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _commands.Count;

        public void Register(ICommand command)
        {
            _commands[command.Metadata.Name] = command;
            if (command is DynamicSkillCommand)
                DynamicSkillNames.Add(command.Metadata.Name);
        }

        public void RegisterAll(IEnumerable<ICommand> commands)
        {
            foreach (var c in commands)
                Register(c);
        }

        public ICommand? GetCommand(string name) =>
            _commands.TryGetValue(name, out var c) ? c : null;

        public ICommand? GetCommand(string name, AgentRuntime runtime) =>
            GetCommand(name) is { } command && command.Metadata.SupportsRuntime(runtime) ? command : null;

        public IEnumerable<ICommand> GetAllCommands() => _commands.Values;

        public IEnumerable<ICommand> GetCommandsByRuntime(AgentRuntime runtime) =>
            _commands.Values.Where(c => c.Metadata.SupportsRuntime(runtime));

        public IEnumerable<ICommand> GetCommandsByCategory(CommandCategory category) =>
            _commands.Values.Where(c => c.Metadata.Category == category);

        public IEnumerable<CommandMetadata> GetAllMetadata() =>
            _commands.Values.Select(c => c.Metadata);

        public bool HasCommand(string name) => _commands.ContainsKey(name);

        public bool Unregister(string name)
        {
            if (!_commands.Remove(name))
                return false;
            DynamicSkillNames.Remove(name);
            return true;
        }
    }
}
