using FluentAssertions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Commands.Discovery;
using Seeing.Agent.Hosting.Commands;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

/// <summary>
/// Markdown 自定义命令的登记契约：用户级 + 项目级目录、项目覆盖用户、文件删除/改动生效、
/// 与已有命令同名时跳过（不静默覆盖）。
/// </summary>
public class MarkdownCommandLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seeing-md-commands-" + Guid.NewGuid().ToString("N"));

    private readonly string _userCommands;
    private readonly string _projectRoot;
    private readonly string _projectCommands;

    public MarkdownCommandLoaderTests()
    {
        _userCommands = Path.Combine(_root, "user", ".seeing", "commands");
        _projectRoot = Path.Combine(_root, "workspace");
        _projectCommands = Path.Combine(_projectRoot, ".seeing", "commands");

        Directory.CreateDirectory(_userCommands);
        Directory.CreateDirectory(_projectCommands);
    }

    [Fact]
    public void Reload_Should_Register_UserAndProject_Commands()
    {
        WriteCommand(_userCommands, "review", "代码评审", "评审 $1");
        WriteCommand(_projectCommands, "deploy", "发布", "发布 {{1}}");

        var registry = new CommandRegistry();
        var loader = CreateLoader(registry);

        loader.Reload().Should().Be(2);

        registry.HasCommand("review").Should().BeTrue("用户级目录 ~/.seeing/commands 必须被扫描");
        registry.HasCommand("deploy").Should().BeTrue("项目级目录 {项目}/.seeing/commands 必须被扫描");

        var deploy = registry.GetCommand("deploy")!;
        deploy.Metadata.Source.Should().NotBeNullOrWhiteSpace("Source 记录文件路径，面板据此归入「自定义命令」组");
        Directory.Exists(Path.GetDirectoryName(deploy.Metadata.Source!)!).Should().BeTrue();
    }

    [Fact]
    public void Reload_Should_Prefer_Project_Command_On_NameConflict()
    {
        WriteCommand(_userCommands, "deploy", "用户版", "用户");
        WriteCommand(_projectCommands, "deploy", "项目版", "项目");

        var registry = new CommandRegistry();
        CreateLoader(registry).Reload();

        registry.GetCommand("deploy")!.Metadata.Description.Should().Be(
            "项目版",
            "同名时项目级覆盖用户级（与技能目录优先级一致）");
    }

    [Fact]
    public void Reload_Should_Unregister_Command_WhenFileDeleted()
    {
        WriteCommand(_projectCommands, "deploy", "发布", "发布");
        var registry = new CommandRegistry();
        var loader = CreateLoader(registry);

        loader.Reload();
        registry.HasCommand("deploy").Should().BeTrue();

        File.Delete(Path.Combine(_projectCommands, "deploy.md"));
        loader.Reload();

        registry.HasCommand("deploy").Should().BeFalse("文件删除后命令必须撤销（否则面板会留幽灵条目）");
    }

    [Fact]
    public void Reload_Should_Pick_Up_ContentChange()
    {
        WriteCommand(_projectCommands, "deploy", "旧描述", "旧");
        var registry = new CommandRegistry();
        var loader = CreateLoader(registry);

        loader.Reload();
        WriteCommand(_projectCommands, "deploy", "新描述", "新");
        loader.Reload();

        registry.GetCommand("deploy")!.Metadata.Description.Should().Be(
            "新描述",
            "MarkdownCommand 持有模板正文，重扫必须替换实例");
    }

    [Fact]
    public void Reload_Should_NotOverwrite_ExistingCommand()
    {
        var registry = new CommandRegistry();
        var builtin = new StubCommand("deploy", "内置版");
        registry.Register(builtin);

        WriteCommand(_projectCommands, "deploy", "自定义版", "自定义");

        CreateLoader(registry).Reload();

        registry.GetCommand("deploy").Should().BeSameAs(builtin,
            "同名冲突必须跳过并告警，不允许静默覆盖已有命令");
    }

    [Fact]
    public async Task ReloadAsync_Should_Ignore_Partial_ConfigChange()
    {
        WriteCommand(_projectCommands, "deploy", "发布", "发布");
        var registry = new CommandRegistry();
        var loader = CreateLoader(registry);
        loader.Reload();

        File.Delete(Path.Combine(_projectCommands, "deploy.md"));
        await loader.ReloadAsync(
            new ConfigChange { ChangedSections = ["Model"] },
            TestContext.Current.CancellationToken);

        registry.HasCommand("deploy").Should().BeTrue("局部配置变更不扫命令目录");

        await loader.ReloadAsync(
            new ConfigChange { ChangedSections = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        registry.HasCommand("deploy").Should().BeFalse("全量配置重载才重扫命令目录");
    }

    [Fact]
    public void ChangeTypes_Should_Cover_Workspace_And_Config()
    {
        var loader = CreateLoader(new CommandRegistry());

        loader.ChangeTypes.Should().Contain(typeof(WorkspaceChange));
        loader.ChangeTypes.Should().Contain(typeof(ConfigChange));
        loader.ComponentId.Should().Be("markdown-commands");
    }

    private MarkdownCommandLoader CreateLoader(CommandRegistry registry)
    {
        var workspace = new WorkspaceProvider();
        workspace.SetInitialWorkspace(_projectRoot, WorkspaceResolutionSource.ManualSwitch);

        return new MarkdownCommandLoader(
            registry,
            new CommandDiscovery(),
            workspace,
            new FakeDirectories(Path.Combine(_root, "user", ".seeing"), Path.Combine(_projectRoot, ".seeing")));
    }

    private static void WriteCommand(string directory, string name, string description, string body)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, name + ".md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeDirectories(string userSeeing, string projectSeeing) : ISeeingDirectories
    {
        public string UserSeeingDirectory { get; } = userSeeing;

        public string ProjectSeeingDirectory { get; } = projectSeeing;
    }

    /// <summary>模拟内置命令（无 SupportedRuntimes → 默认命令）。</summary>
    private sealed class StubCommand(string name, string description) : ICommand
    {
        public CommandMetadata Metadata { get; } = CommandMetadata.Simple(name, description);

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok());
    }
}
