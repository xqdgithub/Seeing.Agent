using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 技能目录核心锁定：双通道 / 根优先级 / 冲突 / 原子快照 / 单飞 / 禁用层级。
/// </summary>
public sealed class SkillCatalogCoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-skill-catalog-{Guid.NewGuid():N}");

    /// <summary>模拟工作区根：项目级技能目录都落在这之下。</summary>
    private readonly string _workspace;

    /// <summary>模拟用户主目录：用户级技能目录都落在这之下。</summary>
    private readonly string _userHome;

    public SkillCatalogCoreTests()
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
            // 临时目录清理失败不影响测试结论（文件仍由系统回收）
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    // ===== 双通道 =====

    [Fact]
    public async Task RegisteredSkill_ShouldWinOverSameNameFile()
    {
        var project = ProjectSkillRoot(0);
        WriteSkill(project, "shared", "shared", "文件版");

        var catalog = CreateCatalog(project);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        catalog.Find("shared")!.Source.Should().Be(SkillSource.ProjectFile);

        var registered = await catalog.RegisterAsync(
            new SkillInfo { Name = "shared", Description = "注册版" },
            "plugin-x",
            TestContext.Current.CancellationToken);

        registered.Should().BeTrue();
        var entry = catalog.Find("shared");
        entry.Should().NotBeNull();
        entry!.Source.Should().Be(SkillSource.Registered);
        entry.ProviderId.Should().Be("plugin-x");
        entry.Info.Description.Should().Be("注册版");
        // 注册覆盖文件技能不得静默：冲突进快照
        catalog.Current.Conflicts.Should().Contain(
            conflict => conflict.Name == "shared" && conflict.Winner == SkillSource.Registered);
    }

    [Fact]
    public async Task ConflictingFile_ShouldBeRecordedAndNotCallable()
    {
        var high = ProjectSkillRoot(0);
        var low = ProjectSkillRoot(2);
        WriteSkill(high, "dup", "dup", "高优先级");
        WriteSkill(low, "dup", "dup", "低优先级");

        var catalog = CreateCatalog(high, low);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        // 同名只保留一个可调用条目（根优先级高者）
        catalog.Current.Entries.Where(e => e.Info.Name == "dup").Should().ContainSingle();
        var entry = catalog.Find("dup");
        entry!.Info.Description.Should().Be("高优先级");
        entry.DirectoryPath.Should().Be(Path.Combine(high, "dup"));

        var conflict = catalog.Current.Conflicts.Should().ContainSingle().Subject;
        conflict.Name.Should().Be("dup");
        conflict.Winner.Should().Be(SkillSource.ProjectFile);
        conflict.WinnerOrigin.Should().Be(Path.Combine(high, "dup"));
        conflict.LoserOrigin.Should().Be(Path.Combine(low, "dup"));
    }

    [Fact]
    public async Task Conflict_ShouldBeVisibleInSnapshot()
    {
        var high = ProjectSkillRoot(0);
        var low = UserSkillRoot(3);
        WriteSkill(high, "clash", "clash", "项目版");
        WriteSkill(low, "clash", "clash", "用户版");

        var catalog = CreateCatalog(high, low);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var snapshot = catalog.Current;
        snapshot.Conflicts.Should().ContainSingle(c => c.Name == "clash");
        snapshot.Conflicts[0].Winner.Should().Be(SkillSource.ProjectFile);
        snapshot.Conflicts[0].Loser.Should().Be(SkillSource.UserFile);

        // 再次刷新（文件未变）仍保留冲突，不会因重建而丢失
        var refreshed = await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        refreshed.Conflicts.Should().ContainSingle(c => c.Name == "clash");
    }

    [Fact]
    public async Task FileRootPrecedence_ShouldBeExplicit()
    {
        // 五个根的优先级（高 → 低）：<ws>/.seeing/skills、<ws>/.agents/skills、<ws>/skills、
        // ~/.seeing/skills、~/.agents/skills
        var roots = new[]
        {
            ProjectSkillRoot(0),
            ProjectSkillRoot(1),
            ProjectSkillRoot(2),
            UserSkillRoot(3),
            UserSkillRoot(4)
        };

        for (var i = 0; i < roots.Length; i++)
            WriteSkill(roots[i], "layered", "layered", $"第 {i} 层");

        var catalog = CreateCatalog(roots);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var entry = catalog.Find("layered");
        entry.Should().NotBeNull();
        entry!.Info.Description.Should().Be("第 0 层");
        entry.DirectoryPath.Should().Be(Path.Combine(roots[0], "layered"));

        // 四个落选者都必须记录冲突（各自来源与路径）
        catalog.Current.Conflicts.Where(c => c.Name == "layered").Should().HaveCount(4);
        catalog.Current.Conflicts.Select(c => c.LoserOrigin)
            .Should().BeEquivalentTo(roots.Skip(1).Select(r => Path.Combine(r, "layered")));

        // 来源分类：项目根 → ProjectFile，用户根 → UserFile
        var conflicts = catalog.Current.Conflicts.ToList();
        conflicts.Count(c => c.Loser == SkillSource.ProjectFile).Should().Be(2);
        conflicts.Count(c => c.Loser == SkillSource.UserFile).Should().Be(2);
    }

    [Fact]
    public async Task RegisteredSkill_WithDuplicateName_ShouldRejectSecond()
    {
        var catalog = CreateCatalog();

        var first = await catalog.RegisterAsync(
            new SkillInfo { Name = "only-once", Description = "第一次" },
            "plugin-a",
            TestContext.Current.CancellationToken);
        var second = await catalog.RegisterAsync(
            new SkillInfo { Name = "only-once", Description = "第二次" },
            "plugin-b",
            TestContext.Current.CancellationToken);

        first.Should().BeTrue();
        second.Should().BeFalse();
        catalog.Find("only-once")!.ProviderId.Should().Be("plugin-a");

        // 注销后缺失名称返回 false
        (await catalog.UnregisterAsync("only-once", TestContext.Current.CancellationToken)).Should().BeTrue();
        catalog.Find("only-once").Should().BeNull();
        (await catalog.UnregisterAsync("only-once", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    // ===== 快照 / 并发 =====

    [Fact]
    public async Task Refresh_ShouldSwapAtomically_NoEmptyWindow()
    {
        var root = ProjectSkillRoot(0);
        WriteSkill(root, "stable", "stable", "稳定技能");

        var catalog = CreateCatalog(root);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        catalog.Current.Entries.Should().ContainSingle();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refreshing = Task.Run(async () =>
        {
            // 反复刷新（每次改文件），期间读者不得看到空快照
            for (var i = 0; i < 20; i++)
            {
                WriteSkill(root, "stable", "stable", $"稳定技能 v{i}");
                await catalog.RefreshAsync(cts.Token);
            }
        }, cts.Token);

        while (!refreshing.IsCompleted)
        {
            catalog.Current.Entries.Should().ContainSingle();
            catalog.Current.Entries[0].Info.Name.Should().Be("stable");
        }

        await refreshing;
        catalog.Current.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task ConcurrentRefresh_ShouldRebuildOnce()
    {
        var root = ProjectSkillRoot(0);
        // 足够多的文件让首次扫描跨越其它调用者的进入时刻（保证真实并发）
        for (var i = 0; i < 60; i++)
            WriteSkill(root, $"skill-{i}", $"skill-{i}", $"技能 {i}");

        var catalog = CreateCatalog(root);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        var versionBefore = catalog.Current.Version;

        var pending = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => catalog.RefreshAsync(TestContext.Current.CancellationToken)))
            .ToArray();
        await Task.WhenAll(pending);

        // 单飞：并发刷新只推进一次版本
        catalog.Current.Version.Should().Be(versionBefore + 1);
        catalog.Current.Entries.Should().HaveCount(60);
    }

    [Fact]
    public async Task Refresh_ShouldRaiseSingleEventPerPublish()
    {
        var root = ProjectSkillRoot(0);
        WriteSkill(root, "stable", "stable", "稳定技能");

        var catalog = CreateCatalog(root);
        var versions = new List<long>();
        using var subscription = catalog.Subscribe(snapshot =>
        {
            // 回调期间快照已经是发布后的版本（锁外触发）
            catalog.Current.Version.Should().Be(snapshot.Version);
            lock (versions)
                versions.Add(snapshot.Version);
        });

        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        versions.Should().Equal(1L, 2L);
    }

    [Fact]
    public async Task Version_ShouldIncreaseMonotonically()
    {
        var root = ProjectSkillRoot(0);
        var catalog = CreateCatalog(root);

        var v1 = (await catalog.RefreshAsync(TestContext.Current.CancellationToken)).Version;
        WriteSkill(root, "later", "later", "后来添加");
        var v2 = (await catalog.RefreshAsync(TestContext.Current.CancellationToken)).Version;

        (await catalog.RegisterAsync(
            new SkillInfo { Name = "registered", Description = "注册技能" },
            "plugin",
            TestContext.Current.CancellationToken)).Should().BeTrue();
        var v3 = catalog.Current.Version;

        (await catalog.UnregisterAsync("registered", TestContext.Current.CancellationToken)).Should().BeTrue();
        var v4 = catalog.Current.Version;

        v2.Should().BeGreaterThan(v1);
        v3.Should().BeGreaterThan(v2);
        v4.Should().BeGreaterThan(v3);
        catalog.Current.Entries.Should().Contain(e => e.Info.Name == "later");
    }

    // ===== 解析失败 / 禁用层级 =====

    [Fact]
    public async Task ParseFailure_ShouldBecomeIssue()
    {
        var root = ProjectSkillRoot(0);
        WriteSkill(root, "good-skill", "good-skill", "正常技能");
        // 无 frontmatter 且目录名不符合名称格式 → 解析失败（不得抛异常）
        var badDirectory = Path.Combine(root, "Bad_Dir");
        Directory.CreateDirectory(badDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(badDirectory, "SKILL.md"),
            "没有 frontmatter 的正文",
            TestContext.Current.CancellationToken);

        var catalog = CreateCatalog(root);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var issue = catalog.Current.Issues.Should().ContainSingle().Subject;
        issue.Path.Should().Be(Path.Combine(badDirectory, "SKILL.md"));
        issue.Reason.Should().NotBeNullOrWhiteSpace();
        // 失败项不进入可调用集合，正常技能不受影响
        catalog.Current.Entries.Should().ContainSingle().Which.Info.Name.Should().Be("good-skill");
        catalog.Find("Bad_Dir").Should().BeNull();
    }

    [Fact]
    public async Task DisabledScopes_ShouldReportLevel()
    {
        // 用户级禁用 alpha、gamma；项目级禁用 beta、alpha（项目覆盖用户上报层级）
        WriteDisabledState(
            Path.Combine(_userHome, ".seeing"),
            ["alpha", "gamma"]);
        WriteDisabledState(
            Path.Combine(_workspace, ".seeing"),
            ["beta", "alpha"]);

        var catalog = CreateCatalog(ProjectSkillRoot(0));
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        var snapshot = catalog.Current;
        snapshot.DisabledNames.Should().BeEquivalentTo("alpha", "beta", "gamma");
        snapshot.DisabledScopes["alpha"].Should().Be(ConfigLevel.Project);
        snapshot.DisabledScopes["beta"].Should().Be(ConfigLevel.Project);
        snapshot.DisabledScopes["gamma"].Should().Be(ConfigLevel.User);

        WriteSkill(ProjectSkillRoot(0), "alpha", "alpha", "被禁用");
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        catalog.Find("alpha").Should().NotBeNull("禁用不影响可见性，只影响可调用性");
        catalog.IsEnabled("alpha").Should().BeFalse();
    }

    [Fact]
    public async Task EnabledEntries_ShouldBeTheSingleProjectionUsedByAllConsumers()
    {
        // F6：可用条目只有一份投影（条目 − 禁用集合），目录接口与钉住快照共用同一实现
        var root = ProjectSkillRoot(0);
        WriteSkill(root, "alpha", "alpha", "启用");
        WriteSkill(root, "beta", "beta", "被禁用");
        WriteDisabledState(Path.Combine(_workspace, ".seeing"), ["beta"]);

        var catalog = CreateCatalog(root);
        var snapshot = await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        catalog.EnabledEntries.Select(entry => entry.Info.Name).Should().BeEquivalentTo("alpha");
        catalog.GetEnabledEntries(snapshot).Select(entry => entry.Info.Name).Should().BeEquivalentTo("alpha");

        // 陈旧快照（执行级钉住）同样按该快照的禁用集合投影，不受目录当前态影响
        snapshot.Entries.Select(entry => entry.Info.Name).Should().BeEquivalentTo("alpha", "beta");
        catalog.GetEnabledEntries(snapshot).Should().HaveCount(1);
    }

    [Fact]
    public async Task CorruptStateFile_ShouldKeepPreviousDisabledSet()
    {
        // I3：损坏 / 撕裂的状态文件不得把「所有技能」重新视为启用（否则管道会叙述假的「新增技能」增量）
        var root = ProjectSkillRoot(0);
        WriteSkill(root, "alpha", "alpha", "被禁用");
        WriteDisabledState(Path.Combine(_workspace, ".seeing"), "alpha");

        using var gate = new SkillWriteGate();
        var catalog = CreateCatalogWithGate(gate, root);
        await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        catalog.Current.DisabledNames.Should().Contain("alpha");

        var statePath = Path.Combine(_workspace, ".seeing", "skill-state.json");
        await File.WriteAllTextAsync(
            statePath,
            "{ \"DisabledSkills\": [\"alph",
            TestContext.Current.CancellationToken);

        var refreshed = await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        refreshed.DisabledNames.Should().Contain("alpha", "损坏 ≠ 全部启用：必须沿用上一次成功解析的禁用集合");
        refreshed.DisabledScopes["alpha"].Should().Be(ConfigLevel.Project);
        catalog.IsEnabled("alpha").Should().BeFalse();

        // 连续两次刷新不得抖动（技能集合与禁用集合都稳定）
        var again = await catalog.RefreshAsync(TestContext.Current.CancellationToken);
        again.DisabledNames.Should().BeEquivalentTo(refreshed.DisabledNames);
        again.Entries.Select(e => e.Info.Name).Should().BeEquivalentTo(refreshed.Entries.Select(e => e.Info.Name));
    }

    // ===== 环境 =====

    private SkillCatalogCore CreateCatalog(params string[] roots)
    {
        var catalog = new SkillCatalogCore(
            NullLogger<SkillCatalogCore>.Instance,
            new FakeDirectories(
                userSeeing: Path.Combine(_userHome, ".seeing"),
                projectSeeing: Path.Combine(_workspace, ".seeing")));

        // 始终显式设置根：避免默认根（真实用户主目录）污染测试
        catalog.SetRoots(roots);
        return catalog;
    }

    /// <summary>与 <c>SkillWriter</c> 共用同一写闸的目录实例（锁定读侧入闸行为）。</summary>
    private SkillCatalogCore CreateCatalogWithGate(SkillWriteGate gate, params string[] roots)
    {
        var catalog = new SkillCatalogCore(
            NullLogger<SkillCatalogCore>.Instance,
            new FakeDirectories(
                userSeeing: Path.Combine(_userHome, ".seeing"),
                projectSeeing: Path.Combine(_workspace, ".seeing")),
            gate);

        catalog.SetRoots(roots);
        return catalog;
    }

    /// <summary>工作区项目级技能根：0=<ws>/.seeing/skills，1=<ws>/.agents/skills，2=<ws>/skills。</summary>
    private string ProjectSkillRoot(int index) => index switch
    {
        0 => Path.Combine(_workspace, ".seeing", "skills"),
        1 => Path.Combine(_workspace, ".agents", "skills"),
        2 => Path.Combine(_workspace, "skills"),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>用户级技能根：3=~/.seeing/skills，4=~/.agents/skills。</summary>
    private string UserSkillRoot(int index) => index switch
    {
        3 => Path.Combine(_userHome, ".seeing", "skills"),
        4 => Path.Combine(_userHome, ".agents", "skills"),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>写入一个合法技能目录（frontmatter + 正文）。</summary>
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

    /// <summary>写入 skill-state.json（沿用既有落盘格式）。</summary>
    private static void WriteDisabledState(string seeingDirectory, params string[] names)
    {
        Directory.CreateDirectory(seeingDirectory);
        var json = """{"DisabledSkills":["""
            + string.Join(",", names.Select(n => $"\"{n}\""))
            + "]}";
        File.WriteAllText(Path.Combine(seeingDirectory, "skill-state.json"), json);
    }

    /// <summary>测试用目录契约：项目级 / 用户级 .seeing 的相对位置可注入。</summary>
    private sealed class FakeDirectories(string userSeeing, string projectSeeing) : ISeeingDirectories
    {
        public string UserSeeingDirectory { get; } = userSeeing;

        public string ProjectSeeingDirectory { get; } = projectSeeing;
    }
}
