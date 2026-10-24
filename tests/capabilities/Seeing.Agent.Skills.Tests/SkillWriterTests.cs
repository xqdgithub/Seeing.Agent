using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 写入收口锁定：只写文件通道 / 路径安全 / 原子写 / 原样写 Markdown / ZIP 前缀剥离 /
/// 启用清所有层级 / skill-state 写闸串行化 / 失败不留临时目录。
/// </summary>
public sealed class SkillWriterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-skill-writer-{Guid.NewGuid():N}");

    /// <summary>模拟工作区根。</summary>
    private readonly string _workspace;

    /// <summary>模拟用户主目录。</summary>
    private readonly string _userHome;

    public SkillWriterTests()
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

    // ===== 创建 =====

    [Fact]
    public async Task Create_ShouldWriteAtomicallyAndRefresh()
    {
        var (catalog, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        var result = await writer.CreateAsync(
            new SkillDraft("demo-skill", "演示技能", "# 正文\n\n内容"),
            ConfigLevel.Project,
            overwrite: false,
            ct);

        result.Success.Should().BeTrue(result.Error ?? "创建应成功");
        result.Entry.Should().NotBeNull();
        result.Entry!.Source.Should().Be(SkillSource.ProjectFile);
        result.Entry.Info.Description.Should().Be("演示技能");

        // 返回时快照已包含新技能（写方法返回即一致）
        catalog.Current.Entries.Should().ContainSingle(e => e.Info.Name == "demo-skill");

        var skillFile = Path.Combine(ProjectSkillsRoot, "demo-skill", "SKILL.md");
        File.Exists(skillFile).Should().BeTrue();
        var written = await File.ReadAllTextAsync(skillFile, ct);
        written.Should().StartWith("---");
        written.Should().Contain("name: demo-skill");
        written.Should().Contain("description: 演示技能");
        written.Should().Contain("# 正文");

        // 原子写：临时目录不得残留
        Directory.GetDirectories(ProjectSkillsRoot, "*.tmp-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Create_OnRegisteredSkill_ShouldFail()
    {
        var (catalog, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        (await catalog.RegisterAsync(
            new SkillInfo { Name = "plugin-skill", Description = "插件技能" },
            "plugin-x",
            ct)).Should().BeTrue();

        var result = await writer.CreateAsync(
            new SkillDraft("plugin-skill", "覆盖尝试", "正文"),
            ConfigLevel.Project,
            overwrite: true,
            ct);

        result.Success.Should().BeFalse();
        result.Entry.Should().BeNull();
        result.Error.Should().Contain("该技能由插件提供");
        Directory.Exists(Path.Combine(ProjectSkillsRoot, "plugin-skill")).Should().BeFalse();
    }

    [Fact]
    public async Task Create_WithPathTraversalName_ShouldFail()
    {
        var (_, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        string[] illegalNames =
        [
            "../escape",
            "..",
            "../..",
            "..\\escape",
            "/abs",
            "C:\\abs",
            "a/b",
            "Bad_Name"
        ];

        foreach (var name in illegalNames)
        {
            var result = await writer.CreateAsync(
                new SkillDraft(name, "非法名称", "正文"),
                ConfigLevel.Project,
                overwrite: true,
                ct);

            result.Success.Should().BeFalse($"名称 {name} 必须被拒绝");
            result.Error.Should().NotBeNullOrWhiteSpace();
        }

        // 任何非法名称都不得在根外产生目录
        Directory.Exists(Path.Combine(_workspace, "escape")).Should().BeFalse();
        Directory.Exists(Path.Combine(_workspace, ".seeing", "skills", "escape")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "escape")).Should().BeFalse();
    }

    // ===== 原样写 Markdown（在线安装"仅 SKILL.md"分支）=====

    [Fact]
    public async Task WriteMarkdown_ShouldPreserveUnknownFrontmatter()
    {
        var (_, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        // 含未知 frontmatter 字段：必须逐字节保留，不得重建 frontmatter
        var raw = """
                  ---
                  name: online-skill
                  description: 在线安装的技能
                  x-custom-field: 必须保留
                  tags: a, b
                  ---

                  正文内容 {{timestamp}} 字面量
                  """;

        var result = await writer.WriteMarkdownAsync(
            "online-skill",
            raw,
            ConfigLevel.User,
            overwrite: false,
            ct);

        result.Success.Should().BeTrue(result.Error ?? "写入应成功");
        result.Entry.Should().NotBeNull();
        result.Entry!.Source.Should().Be(SkillSource.UserFile);

        var path = Path.Combine(UserSkillsRoot, "online-skill", "SKILL.md");
        var written = await File.ReadAllTextAsync(path, ct);
        written.Should().Be(raw);
        written.Should().Contain("x-custom-field: 必须保留");
    }

    // ===== ZIP 安装 =====

    [Fact]
    public async Task InstallZip_WithSubdirectoryPrefix_ShouldStripRepoRoot()
    {
        var (catalog, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        using var zip = BuildZip(
            ("repo-main/README.md", "仓库说明"),
            ("repo-main/skills/zip-skill/SKILL.md",
                "---\nname: zip-skill\ndescription: 压缩包技能\n---\n\n正文"),
            ("repo-main/skills/zip-skill/reference.md", "# 参考"),
            ("repo-main/skills/other/SKILL.md",
                "---\nname: other\ndescription: 不应安装\n---\n\n正文"));

        var result = await writer.InstallZipAsync(
            "zip-skill",
            zip,
            ConfigLevel.Project,
            overwrite: false,
            subdirectoryPrefix: "repo-main/skills/zip-skill",
            ct);

        result.Success.Should().BeTrue(result.Error ?? "安装应成功");
        result.Entry.Should().NotBeNull();
        result.Entry!.Source.Should().Be(SkillSource.ProjectFile);

        var directory = Path.Combine(ProjectSkillsRoot, "zip-skill");
        File.Exists(Path.Combine(directory, "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(directory, "reference.md")).Should().BeTrue();

        // 前缀之外的文件（README / 其它技能）不得落入目标目录
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Should().HaveCount(2);
        catalog.Find("zip-skill").Should().NotBeNull();
    }

    [Fact]
    public async Task InstallZip_WithZipSlipEntry_ShouldFailAndWriteNothing()
    {
        var (catalog, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        using var zip = BuildZip(
            ("skill/SKILL.md", "---\nname: slip-skill\ndescription: 路径穿越\n---\n\n正文"),
            ("skill/../../evil.txt", "逃逸"));

        var result = await writer.InstallZipAsync(
            "slip-skill",
            zip,
            ConfigLevel.Project,
            overwrite: false,
            subdirectoryPrefix: "skill",
            ct);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("非法路径");
        Directory.Exists(Path.Combine(ProjectSkillsRoot, "slip-skill")).Should().BeFalse();
        Directory.GetDirectories(ProjectSkillsRoot, "*.tmp-*").Should().BeEmpty();
        catalog.Find("slip-skill").Should().BeNull();
    }

    // ===== 启用 / 禁用 =====

    [Fact]
    public async Task SetEnabled_ShouldClearAllLevels()
    {
        var (catalog, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        WriteSkillFile(ProjectSkillsRoot, "alpha", "跨层禁用");
        WriteDisabledState(ProjectSeeingDirectory, "alpha");
        WriteDisabledState(UserSeeingDirectory, "alpha");
        await catalog.RefreshAsync(ct);

        catalog.IsEnabled("alpha").Should().BeFalse();
        catalog.Current.DisabledScopes["alpha"].Should().Be(ConfigLevel.Project);

        // 启用：必须清除两个层级的禁用项（否则"双层级禁用"永远启用不了）
        var enabled = await writer.SetEnabledAsync("alpha", enabled: true, ConfigLevel.Project, ct);

        enabled.Success.Should().BeTrue(enabled.Error ?? "启用应成功");
        enabled.Entry.Should().NotBeNull();
        catalog.IsEnabled("alpha").Should().BeTrue();
        catalog.Current.DisabledNames.Should().BeEmpty();
        ReadDisabledState(ProjectSeeingDirectory).Should().BeEmpty();
        ReadDisabledState(UserSeeingDirectory).Should().BeEmpty();

        // 禁用：只写调用方指定的层级
        var disabled = await writer.SetEnabledAsync("alpha", enabled: false, ConfigLevel.User, ct);

        disabled.Success.Should().BeTrue(disabled.Error ?? "禁用应成功");
        catalog.IsEnabled("alpha").Should().BeFalse();
        catalog.Current.DisabledScopes["alpha"].Should().Be(ConfigLevel.User);
        ReadDisabledState(UserSeeingDirectory).Should().BeEquivalentTo("alpha");
        ReadDisabledState(ProjectSeeingDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentToggles_ShouldSerialize()
    {
        var (catalog, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        var names = Enumerable.Range(0, 8).Select(i => $"skill-{i}").ToArray();
        foreach (var name in names)
            WriteSkillFile(UserSkillsRoot, name, $"并发禁用 {name}");

        await catalog.RefreshAsync(ct);

        // 并发禁用不同技能：读-改-写必须串行，否则后写者覆盖前写者（丢更新）
        var results = await Task.WhenAll(names.Select(name =>
            Task.Run(() => writer.SetEnabledAsync(name, enabled: false, ConfigLevel.User, ct), ct)));

        results.Should().OnlyContain(r => r.Success);
        ReadDisabledState(UserSeeingDirectory).Should().BeEquivalentTo(names);
        catalog.Current.DisabledNames.Should().BeEquivalentTo(names);
        catalog.IsEnabled(names[0]).Should().BeFalse();
    }

    // ===== 失败清理 =====

    [Fact]
    public async Task FailedWrite_ShouldNotLeaveTempDirectory()
    {
        var (_, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        // 目标路径被同名「文件」占用：临时目录写完后的 Directory.Move 必然失败
        Directory.CreateDirectory(ProjectSkillsRoot);
        var blocker = Path.Combine(ProjectSkillsRoot, "blocked-skill");
        await File.WriteAllTextAsync(blocker, "占位文件", ct);

        var result = await writer.CreateAsync(
            new SkillDraft("blocked-skill", "无法落位", "正文"),
            ConfigLevel.Project,
            overwrite: false,
            ct);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        File.Exists(blocker).Should().BeTrue("占位文件不得被破坏");
        Directory.GetDirectories(ProjectSkillsRoot, "*.tmp-*").Should().BeEmpty();
        Directory.GetDirectories(ProjectSkillsRoot, "*.bak-*").Should().BeEmpty();
    }

    // ===== UI 投影（仓库无 bUnit：锁定页面渲染所依赖的目录事件 / Source 投影） =====

    [Fact]
    public async Task CatalogEvent_ShouldExposeSourceProjectionForUi()
    {
        var (catalog, writer) = CreateWriter();
        var ct = TestContext.Current.CancellationToken;

        var published = new List<SkillCatalogSnapshot>();
        using var subscription = catalog.Subscribe(snapshot =>
        {
            lock (published)
                published.Add(snapshot);
        });

        await writer.CreateAsync(
            new SkillDraft("ui-skill", "UI 投影", "正文"),
            ConfigLevel.Project,
            overwrite: false,
            ct);

        (await catalog.RegisterAsync(
            new SkillInfo { Name = "ui-builtin", Description = "内置技能" },
            "plugin-x",
            ct)).Should().BeTrue();

        // 禁用后事件快照必须带禁用集合：页面直接渲染快照，不做本地缓存
        await writer.SetEnabledAsync("ui-skill", enabled: false, ConfigLevel.Project, ct);

        SkillCatalogSnapshot last;
        lock (published)
            last = published[^1];

        last.Entries.Select(entry => (entry.Info.Name, entry.Source)).Should().BeEquivalentTo(
            [
                ("ui-skill", SkillSource.ProjectFile),
                ("ui-builtin", SkillSource.Registered)
            ]);
        last.DisabledNames.Should().Contain("ui-skill");
        last.DisabledScopes["ui-skill"].Should().Be(ConfigLevel.Project);

        lock (published)
            published.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    // ===== 环境 =====

    private string ProjectSkillsRoot => Path.Combine(ProjectSeeingDirectory, "skills");

    private string UserSkillsRoot => Path.Combine(UserSeeingDirectory, "skills");

    private string ProjectSeeingDirectory => Path.Combine(_workspace, ".seeing");

    private string UserSeeingDirectory => Path.Combine(_userHome, ".seeing");

    private (SkillCatalogCore Catalog, SkillWriter Writer) CreateWriter()
    {
        var directories = new FakeDirectories(UserSeeingDirectory, ProjectSeeingDirectory);
        var catalog = new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance, directories);

        // 显式设置根：避免默认根（真实用户主目录）污染测试
        catalog.SetRoots([ProjectSkillsRoot, UserSkillsRoot]);

        var writer = new SkillWriter(
            NullLogger<SkillWriter>.Instance,
            catalog,
            directories,
            new SkillWriteGate());

        return (catalog, writer);
    }

    /// <summary>写一个合法技能目录（frontmatter + 正文）。</summary>
    private static void WriteSkillFile(string root, string name, string description)
    {
        var directory = Path.Combine(root, name);
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

    /// <summary>写 skill-state.json（沿用既有落盘格式）。</summary>
    private static void WriteDisabledState(string seeingDirectory, params string[] names)
    {
        Directory.CreateDirectory(seeingDirectory);
        var json = """{"DisabledSkills":["""
            + string.Join(",", names.Select(n => $"\"{n}\""))
            + "]}";
        File.WriteAllText(Path.Combine(seeingDirectory, "skill-state.json"), json);
    }

    /// <summary>读出 skill-state.json 的禁用名称集合。</summary>
    private static IReadOnlyList<string> ReadDisabledState(string seeingDirectory)
    {
        var path = Path.Combine(seeingDirectory, "skill-state.json");
        if (!File.Exists(path))
            return [];

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("DisabledSkills", out var array))
            return [];

        return array.EnumerateArray()
            .Select(item => item.GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }

    /// <summary>构造内存 ZIP（路径使用 / 分隔，模拟 GitHub 仓库布局）。</summary>
    private static MemoryStream BuildZip(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>测试用目录契约。</summary>
    private sealed class FakeDirectories(string userSeeing, string projectSeeing) : ISeeingDirectories
    {
        public string UserSeeingDirectory { get; } = userSeeing;

        public string ProjectSeeingDirectory { get; } = projectSeeing;
    }
}
