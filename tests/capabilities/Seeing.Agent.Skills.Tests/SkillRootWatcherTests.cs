using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Agent.Skills.Configuration;
using Seeing.Agent.Skills.Watching;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 技能根 watcher 锁定：多根监听 / 去抖合并 / 缺根重挂 / 释放后静默。
/// <para>watcher 依赖真实文件系统事件，所有断言都用「轮询 + 5s 上限」，不使用固定长睡眠。</para>
/// </summary>
public sealed class SkillRootWatcherTests : IDisposable
{
    /// <summary>去抖窗口（与生产默认值一致）。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    /// <summary>单次等待上限（计划要求 5s 封顶）。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const int PollIntervalMilliseconds = 25;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"seeing-skill-watch-{Guid.NewGuid():N}");

    public SkillRootWatcherTests()
    {
        Directory.CreateDirectory(_root);
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
            // 临时目录清理失败不影响测试结论（文件由系统回收）
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ===== 回调触发 =====

    [Fact]
    public async Task CreateSkillFile_ShouldTriggerOneCallbackAfterDebounce()
    {
        // Arrange：根已存在，写入一个技能，突发事件必须合并为一次回调
        var skillsRoot = Path.Combine(_root, "skills");
        Directory.CreateDirectory(skillsRoot);

        var hits = 0;
        using var watcher = new SkillRootWatcher(
            [skillsRoot],
            _ =>
            {
                Interlocked.Increment(ref hits);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            Debounce);

        // Act
        WriteSkill(skillsRoot, "alpha");

        // Assert：至少触发一次，且静默窗口后不得继续抖动
        (await WaitUntilAsync(() => Volatile.Read(ref hits) >= 1)).Should().BeTrue(
            "创建技能文件应触发回调");
        await Task.Delay(Debounce * 2, Ct);
        Volatile.Read(ref hits).Should().Be(1, "一次创建（目录 + 文件）应合并为一次回调");
    }

    [Fact]
    public async Task RapidWrites_ShouldCoalesceIntoOneCallback()
    {
        var skillsRoot = Path.Combine(_root, "skills");
        Directory.CreateDirectory(skillsRoot);
        var skillFile = WriteSkill(skillsRoot, "beta");

        var hits = 0;
        using var watcher = new SkillRootWatcher(
            [skillsRoot],
            _ =>
            {
                Interlocked.Increment(ref hits);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            Debounce);

        // Act：连续快速写入，间隔远小于去抖窗口
        for (var i = 0; i < 8; i++)
        {
            await File.WriteAllTextAsync(skillFile, $"正文 v{i}", Ct);
            await Task.Delay(40, Ct);
        }

        // Assert
        (await WaitUntilAsync(() => Volatile.Read(ref hits) >= 1)).Should().BeTrue(
            "连续写入应触发回调");
        await Task.Delay(Debounce * 2, Ct);
        Volatile.Read(ref hits).Should().Be(1, "去抖窗口内的成串事件必须合并为一次回调");
    }

    [Fact]
    public async Task DeleteSkillDirectory_ShouldTriggerCallback()
    {
        var skillsRoot = Path.Combine(_root, "skills");
        Directory.CreateDirectory(skillsRoot);

        var hits = 0;
        using var watcher = new SkillRootWatcher(
            [skillsRoot],
            _ =>
            {
                Interlocked.Increment(ref hits);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            Debounce);

        // 先建技能：确认监听已生效，再验证删除事件
        var skillFile = WriteSkill(skillsRoot, "gamma");
        var skillDirectory = Path.GetDirectoryName(skillFile)!;
        (await WaitUntilAsync(() => Volatile.Read(ref hits) >= 1)).Should().BeTrue("创建技能应触发回调");
        var afterCreate = Volatile.Read(ref hits);

        // Act
        Directory.Delete(skillDirectory, recursive: true);

        // Assert
        (await WaitUntilAsync(() => Volatile.Read(ref hits) > afterCreate)).Should().BeTrue(
            "删除技能目录应触发回调");
    }

    [Fact]
    public async Task MissingRoot_ShouldNotThrow_AndAttachWhenCreated()
    {
        // Arrange：根尚不存在（安装器写到一半 / 首次创建），构造与启动都不得抛
        var missingRoot = Path.Combine(_root, "a", "b", "skills");

        var hits = 0;
        using var watcher = new SkillRootWatcher(
            [missingRoot],
            _ =>
            {
                Interlocked.Increment(ref hits);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            Debounce);

        // Act 1：根被创建 —— 应经最近存在祖先观察到并重挂
        Directory.CreateDirectory(missingRoot);
        (await WaitUntilAsync(() => Volatile.Read(ref hits) >= 1)).Should().BeTrue(
            "根目录被创建后应触发回调");

        // Act 2：重挂完成后，新根内的文件事件必须被捕获
        var afterCreate = Volatile.Read(ref hits);
        WriteSkill(missingRoot, "delta");

        // Assert
        (await WaitUntilAsync(() => Volatile.Read(ref hits) > afterCreate)).Should().BeTrue(
            "根出现后必须重挂到根本身，后续文件事件不得丢失");
    }

    [Fact]
    public async Task Dispose_ShouldStopCallbacks()
    {
        var skillsRoot = Path.Combine(_root, "skills");
        Directory.CreateDirectory(skillsRoot);

        var hits = 0;
        var watcher = new SkillRootWatcher(
            [skillsRoot],
            _ =>
            {
                Interlocked.Increment(ref hits);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            Debounce);

        WriteSkill(skillsRoot, "epsilon");
        (await WaitUntilAsync(() => Volatile.Read(ref hits) >= 1)).Should().BeTrue("初始变更应触发回调");

        // Act
        watcher.Dispose();
        var afterDispose = Volatile.Read(ref hits);
        WriteSkill(skillsRoot, "zeta");

        // Assert：释放后不得再有任何回调
        await Task.Delay(Debounce * 3, Ct);
        Volatile.Read(ref hits).Should().Be(afterDispose, "Dispose 之后必须停止全部回调");
    }

    // ===== 加载器幂等路径 =====

    [Fact]
    public async Task WorkspaceSwitch_ShouldDropOldRoots()
    {
        // Arrange：两个工作区各有一个技能，加载路径必须只重算根、不保留旧根
        var workspaceA = Path.Combine(_root, "ws-a");
        var workspaceB = Path.Combine(_root, "ws-b");
        WriteSkill(Path.Combine(workspaceA, "skills"), "alpha");
        WriteSkill(Path.Combine(workspaceB, "skills"), "beta");

        var catalog = new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance);
        var monitor = new MutableOptionsMonitor();
        monitor.Current = new SkillsOptions { Paths = ["skills"] };

        var provider = new StubServiceProvider(new Dictionary<Type, object>
        {
            [typeof(SkillCatalogCore)] = catalog,
            [typeof(ISkillCatalog)] = catalog,
            [typeof(IOptionsMonitor<SkillsOptions>)] = monitor,
            [typeof(ISeeingDirectories)] = new FakeDirectories(
                userSeeing: Path.Combine(_root, "home", ".seeing"),
                projectSeeing: Path.Combine(_root, "proj", ".seeing")),
        });

        var loader = new SkillLoader();

        // Act 1：工作区 A
        var first = await loader.LoadAsync(provider, workspaceA, Ct);

        // Assert 1
        first.Success.Should().BeTrue();
        first.Details.Should().Contain("alpha");
        catalog.Find("alpha").Should().NotBeNull();

        // Act 2：切到工作区 B（ReloadAsync 与 LoadAsync 同一幂等路径）
        var second = await loader.ReloadAsync(provider, workspaceB, Ct);

        // Assert 2：旧根被丢弃，新根生效
        second.Success.Should().BeTrue();
        second.Details.Should().Contain("beta").And.NotContain("alpha");
        catalog.Find("beta").Should().NotBeNull();
        catalog.Find("alpha").Should().BeNull("切换工作区后旧根技能不得残留");
    }

    // ===== 环境 =====

    /// <summary>轮询等待条件成立（超时即返回 false，由断言给出可读失败信息）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(PollIntervalMilliseconds, Ct);
        }

        return condition();
    }

    /// <summary>写入一个合法技能目录（frontmatter + 正文），返回 SKILL.md 路径。</summary>
    private static string WriteSkill(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "SKILL.md");
        File.WriteAllText(
            file,
            $"""
             ---
             name: {name}
             description: {name} 技能
             ---

             正文 {name}
             """);
        return file;
    }

    /// <summary>可变技能配置（模拟热重载 / 工作区切换）。</summary>
    private sealed class MutableOptionsMonitor : IOptionsMonitor<SkillsOptions>
    {
        public SkillsOptions Current { get; set; } = new();

        public SkillsOptions CurrentValue => Current;

        public SkillsOptions Get(string? name) => Current;

        public IDisposable? OnChange(Action<SkillsOptions, string?> listener) => null;
    }

    /// <summary>最小服务定位器：只提供加载器需要的几个契约。</summary>
    private sealed class StubServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            services.TryGetValue(serviceType, out var instance) ? instance : null;
    }

    /// <summary>测试用目录契约：项目级 / 用户级 .seeing 的相对位置可注入。</summary>
    private sealed class FakeDirectories(string userSeeing, string projectSeeing) : ISeeingDirectories
    {
        public string UserSeeingDirectory { get; } = userSeeing;

        public string ProjectSeeingDirectory { get; } = projectSeeing;
    }
}
