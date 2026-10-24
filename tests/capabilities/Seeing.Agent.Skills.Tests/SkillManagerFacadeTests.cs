using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Agent.Skills.Commands;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 只读门面锁定：<see cref="ISkillManager"/> 只保留只读成员（写面编译期拦截）/ 动态命令执行时按名解析 /
/// 目录事件驱动的命令同步（删除与被禁用都必须注销，修掉旧 bug）。
/// </summary>
public sealed class SkillManagerFacadeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-skill-facade-{Guid.NewGuid():N}");

    private readonly string _workspace;

    private readonly string _userHome;

    public SkillManagerFacadeTests()
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
            // 临时目录清理失败不影响测试结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    // ===== 契约形状 =====

    [Fact]
    public void ISkillManager_ShouldExposeNoWritableMembers()
    {
        string[] writeMembers =
        [
            nameof(ISkillManager.AddSearchDirectory),
            nameof(ISkillManager.DiscoverSkillsAsync),
            nameof(ISkillManager.RegisterEmbeddedSkill),
            nameof(ISkillManager.Unregister)
        ];

        foreach (var name in writeMembers)
        {
            var method = typeof(ISkillManager).GetMethod(name);
            method.Should().NotBeNull($"{name} 应保留签名以便诊断，但必须被标注为过期");

            var obsolete = method!.GetCustomAttribute<ObsoleteAttribute>();
            obsolete.Should().NotBeNull($"{name} 必须标注 [Obsolete]（类型系统要拦住第二条写路径）");
            obsolete!.IsError.Should().BeTrue($"{name} 必须是 error:true，调用即编译失败");
            obsolete.Message.Should().Contain("ISkillCatalog", "过期信息必须指向唯一真相源");
        }

        string[] readMembers =
        [
            nameof(ISkillManager.GetSkillDirectories),
            nameof(ISkillManager.GetSkillInfo),
            nameof(ISkillManager.GetAllSkillInfos),
            nameof(ISkillManager.GetAllSkillInfosIncludingDisabled)
        ];

        foreach (var name in readMembers)
        {
            var method = typeof(ISkillManager).GetMethod(name);
            method.Should().NotBeNull($"{name} 必须保留");
            method!.GetCustomAttribute<ObsoleteAttribute>()
                .Should().BeNull($"{name} 是只读成员，不得过期");
        }
    }

    // ===== 命令按名解析 =====

    [Fact]
    public async Task DynamicCommand_ShouldResolveByNameAtExecution()
    {
        var catalog = CreateCatalog();
        await catalog.RegisterAsync(
            new SkillInfo { Name = "demo", Description = "d", Content = "第一版 $1" },
            "test",
            TestContext.Current.CancellationToken);

        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("/demo arg"));
        var sessionManager = CreateSessionManager(session);

        using var provider = new ServiceCollection()
            .AddSingleton<ISkillCatalog>(catalog)
            .BuildServiceProvider();

        // 命令只持有名称：正文在目录里，不复制
        var command = new DynamicSkillCommand(sessionManager.Object, "demo");

        var first = await command.ExecuteAsync(
            new CommandContext
            {
                SessionId = "s1",
                Input = "/demo arg",
                Arguments = "arg",
                Services = provider
            },
            TestContext.Current.CancellationToken);

        first.Success.Should().BeTrue();
        session.Messages[^1].Content.Should().Contain("第一版 arg");

        // 目录内容变化后（注销旧版 + 注册新版），同一条命令必须读到新正文
        (await catalog.UnregisterAsync("demo", TestContext.Current.CancellationToken)).Should().BeTrue();
        await catalog.RegisterAsync(
            new SkillInfo { Name = "demo", Description = "d", Content = "第二版 $1" },
            "test",
            TestContext.Current.CancellationToken);

        session.AddMessage(SessionMessage.UserMessage("/demo arg"));
        var second = await command.ExecuteAsync(
            new CommandContext
            {
                SessionId = "s1",
                Input = "/demo arg",
                Arguments = "arg",
                Services = provider
            },
            TestContext.Current.CancellationToken);

        second.Success.Should().BeTrue();
        session.Messages[^1].Content.Should().Contain("第二版 arg", "命令不得持有 SkillInfo 副本");
    }

    // ===== 目录事件驱动命令同步 =====

    [Fact]
    public async Task DeletedSkillCommand_ShouldBeUnregistered_OnCatalogEvent()
    {
        var catalog = CreateCatalog();
        await catalog.RegisterAsync(
            new SkillInfo { Name = "demo", Description = "d", Content = "c" },
            "test",
            TestContext.Current.CancellationToken);

        var (provider, registry, module) = await ActivateAsync(catalog);
        try
        {
            registry.HasCommand("demo").Should().BeTrue("激活时应按目录快照登记动态命令");

            (await catalog.UnregisterAsync("demo", TestContext.Current.CancellationToken))
                .Should().BeTrue();

            registry.HasCommand("demo").Should().BeFalse("技能被删除后命令必须随目录事件撤销");
        }
        finally
        {
            await module.DeactivateAsync(provider);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisabledSkillCommand_ShouldAlsoBeUnregistered()
    {
        // 先用注册通道造出技能，再让目录读到「禁用」状态（两层 skill-state.json 的并集）
        var catalog = CreateCatalog();
        await catalog.RegisterAsync(
            new SkillInfo { Name = "demo", Description = "d", Content = "c" },
            "test",
            TestContext.Current.CancellationToken);

        var (provider, registry, module) = await ActivateAsync(catalog);
        try
        {
            registry.HasCommand("demo").Should().BeTrue();

            // 禁用：写项目级状态文件后重扫目录（事件驱动同步应撤销命令）
            WriteDisabledState(Path.Combine(_workspace, ".seeing"), "demo");
            await catalog.RefreshAsync(TestContext.Current.CancellationToken);

            catalog.IsEnabled("demo").Should().BeFalse();
            registry.HasCommand("demo").Should().BeFalse(
                "被禁用的技能同样不可调用，命令必须注销（旧实现只按启用集合求差，永远注销不掉）");

            // 恢复启用后命令重新登记
            WriteDisabledState(Path.Combine(_workspace, ".seeing"));
            await catalog.RefreshAsync(TestContext.Current.CancellationToken);

            registry.HasCommand("demo").Should().BeTrue();
        }
        finally
        {
            await module.DeactivateAsync(provider);
            await provider.DisposeAsync();
        }
    }

    // ===== 辅助 =====

    /// <summary>带两层禁用状态的目录（项目级 / 用户级 .seeing 落在临时目录下）。</summary>
    private SkillCatalogCore CreateCatalog()
    {
        var catalog = new SkillCatalogCore(
            NullLogger<SkillCatalogCore>.Instance,
            new FakeDirectories(
                userSeeing: Path.Combine(_userHome, ".seeing"),
                projectSeeing: Path.Combine(_workspace, ".seeing")));

        // 显式设置根，避免默认根（真实用户主目录）污染测试
        catalog.SetRoots([Path.Combine(_workspace, "skills")]);
        return catalog;
    }

    private static async Task<(ServiceProvider Provider, FakeCommandRegistry Registry, SkillsModule Module)>
        ActivateAsync(SkillCatalogCore catalog)
    {
        var registry = new FakeCommandRegistry();
        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton<ISkillCatalog>(catalog);
        services.AddSingleton<ICommandRegistry>(registry);
        services.AddSingleton(Mock.Of<ISessionManager>());

        var provider = services.BuildServiceProvider();
        var module = new SkillsModule();
        await module.ActivateAsync(provider);
        return (provider, registry, module);
    }

    private static Mock<ISessionManager> CreateSessionManager(SessionData session)
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        mock.Setup(m => m.UpdateSessionAsync(
                session.Id,
                It.IsAny<Action<SessionData>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Action<SessionData>, CancellationToken>((_, action, _) => action(session))
            .ReturnsAsync(session);
        return mock;
    }

    /// <summary>写入 skill-state.json（传空表示无禁用项）。</summary>
    private static void WriteDisabledState(string seeingDirectory, params string[] names)
    {
        Directory.CreateDirectory(seeingDirectory);
        var json = """{"DisabledSkills":["""
            + string.Join(",", names.Select(n => $"\"{n}\""))
            + "]}";
        File.WriteAllText(Path.Combine(seeingDirectory, "skill-state.json"), json);
    }

    /// <summary>测试用目录契约。</summary>
    private sealed class FakeDirectories(string userSeeing, string projectSeeing) : ISeeingDirectories
    {
        public string UserSeeingDirectory { get; } = userSeeing;

        public string ProjectSeeingDirectory { get; } = projectSeeing;
    }

    /// <summary>最小命令注册表：只服务于「登记 / 撤销 / 存在性」断言。</summary>
    private sealed class FakeCommandRegistry : ICommandRegistry
    {
        private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

        public void Register(ICommand command) => _commands[command.Metadata.Name] = command;

        public void RegisterAll(IEnumerable<ICommand> commands)
        {
            foreach (var command in commands)
                Register(command);
        }

        public ICommand? GetCommand(string name) =>
            _commands.TryGetValue(name, out var command) ? command : null;

        public ICommand? GetCommand(string name, Seeing.Agent.Abstractions.Agents.AgentRuntime runtime) =>
            GetCommand(name) is { } command && command.Metadata.SupportsRuntime(runtime) ? command : null;

        public IEnumerable<ICommand> GetAllCommands() => _commands.Values.ToList();

        public IEnumerable<ICommand> GetCommandsByRuntime(Seeing.Agent.Abstractions.Agents.AgentRuntime runtime) =>
            _commands.Values.Where(command => command.Metadata.SupportsRuntime(runtime)).ToList();

        public IEnumerable<ICommand> GetCommandsByCategory(CommandCategory category) =>
            _commands.Values.Where(command => command.Metadata.Category == category).ToList();

        public IEnumerable<CommandMetadata> GetAllMetadata() =>
            _commands.Values.Select(command => command.Metadata).ToList();

        public bool HasCommand(string name) => _commands.ContainsKey(name);

        public bool Unregister(string name) => _commands.Remove(name);

        public int Count => _commands.Count;
    }
}
