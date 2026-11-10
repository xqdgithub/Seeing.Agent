using FluentAssertions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Ui;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.Shell;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Shell;

/// <summary>
/// 设置卡与插槽迁移到 UI 信封后的完整性与等价性守门（批次 D）。
/// <para>
/// 钉住四件事：
/// </para>
/// <list type="number">
/// <item>设置页仍是 4 张卡，Tab key（<c>{OwnerId}.{Id}</c>）全进程唯一。</item>
/// <item>4 个插槽点都有宿主声明，文件面板这条由 <c>files.workspace</c> 归属。</item>
/// <item>插槽随会话工作模式收窄；设置卡（Process 作用域）不受影响。</item>
/// <item>两个消费点都用 <c>ErrorBoundary</c> 隔离模块组件，且 <c>src/</c> 不再出现旧类型名。</item>
/// </list>
/// </summary>
public class SettingsAndSlotMigrationTests
{
    private const string HostOwnerId = "host.webui";

    private static readonly string[] s_legacyTypeNames =
    [
        "LegacySettingsCardContribution",
        "LegacySlotContribution",
        "MessageRendererContribution",
        "IUiContributionCatalog",
    ];

    [Fact]
    public void SettingsCards_ShouldBeFourWithUniqueOwnerScopedKeys()
    {
        var registry = BuildRegistry();

        var cards = registry.All
            .Where(e => e.Contribution is SettingsCardContribution)
            .ToList();

        cards.Should().HaveCount(4, "设置页固定 4 张模块卡片（启动能力 / 工作模式 / 调度器 / 记忆）");

        cards.Select(e => e.Contribution.Id).Should().Equal(
            "settings.boot", "settings.scenarios", "settings.scheduler", "settings.memory");

        // Tab key = {OwnerId}.{Id}：不同创建方的同 id 不会撞 key
        var keys = cards.Select(e => $"{e.OwnerId}.{e.Contribution.Id}").ToList();
        keys.Should().OnlyHaveUniqueItems("Tab key 重复会让 Blazor 的 @key 冲突并直接渲染崩");
    }

    [Fact]
    public void SettingsCards_ShouldAllResolveToHostViews()
    {
        var views = ResolveViews();

        foreach (var entry in BuildRegistry().All.Where(e => e.Contribution is SettingsCardContribution))
        {
            var card = (SettingsCardContribution)entry.Contribution;
            views.CanResolve(card.ViewKey).Should().BeTrue(
                $"设置卡 '{card.Id}' 的视图键必须已登记，否则页签渲染为空");
        }
    }

    [Fact]
    public void SettingsCards_ShouldDeclareOwnershipForGatedCards()
    {
        var cards = BuildRegistry().All
            .Where(e => e.Contribution is SettingsCardContribution)
            .ToDictionary(
                e => e.Contribution.Id,
                e => e.Contribution.Requires);

        cards["settings.boot"].Should().BeEmpty("启动能力卡是壳层的，必须在任何 Boot 配置下可达");
        cards["settings.scenarios"].Should().BeEmpty();
        cards["settings.scheduler"].Should().Equal("scheduler");
        cards["settings.memory"].Should().Equal("memory");
    }

    [Fact]
    public void SlotHost_ShouldOnlyMapRemainingSlots()
    {
        // 文件面板已从插槽迁到工作台；剩余 3 个插槽仍是通用挂载位
        (string SlotName, string PointId)[] expected =
        [
            ("session_prompt", "session.prompt"),
            ("session_toolbar_actions", "session.toolbar.actions"),
            ("message_toolbar", "session.input.toolbar"),
        ];

        foreach (var (slotName, pointId) in expected)
        {
            var point = WebUiPoints.All.FirstOrDefault(p => p.Id == pointId);
            point.Should().NotBeNull($"槽 '{slotName}' 映射到未声明的扩展点 '{pointId}'");
            point!.PayloadKind.Should().Be(UiPayloadKind.Slot);
            point.Scope.Should().Be(UiVisibilityScope.Session,
                $"槽 '{slotName}' 必须随会话工作模式收窄（Scope=Session）");
        }
    }

    [Fact]
    public void WorkbenchDockPoint_ShouldBeSessionScopedWithWorkbenchPayload()
    {
        var point = WebUiPoints.All.Should()
            .ContainSingle(p => p.Id == Seeing.Agent.Abstractions.Ui.Workbench.WorkbenchPoints.DockId)
            .Subject;

        point.PayloadKind.Should().Be(UiPayloadKind.Workbench);
        point.Scope.Should().Be(UiVisibilityScope.Session,
            "工作台与插槽同语义：随会话工作模式收窄");
    }

    [Fact]
    public void WorkbenchContributions_ShouldBeOwnedByTheirProvidingModule()
    {
        // 文件工作台与 Markdown 预览都由 files.workspace 声明（同一个模块，两块视图）
        var module = new Seeing.Agent.Files.FileWorkspaceModule();
        var contributions = module.DescribeUi().OfType<Seeing.Agent.Abstractions.Ui.Workbench.WorkbenchContribution>().ToList();

        contributions.Should().HaveCount(2);
        contributions.Select(c => c.Id).Should().BeEquivalentTo("files", "markdown");

        foreach (var contribution in contributions)
        {
            contribution.Point.Should().Be(Seeing.Agent.Abstractions.Ui.Workbench.WorkbenchPoints.DockId);
            contribution.Requires.Should().Equal(["files.workspace"], "归属必须是提供它的模块");
            contribution.ViewKey.Should().StartWith(Seeing.Agent.Abstractions.Ui.Workbench.WorkbenchPoints.ViewKeyPrefix);
            contribution.OpenPayloadSchema.Should().NotBeNull("工作台必须给 agent 一个「该传什么」的提示");
        }
    }

    [Fact]
    public void WorkbenchViews_ShouldBeRegisteredAndDeclareViewContext()
    {
        var views = new Seeing.Agent.WebUI.Workbench.WorkbenchViewRegistry();
        var resolver = new WebUiViewResolver();
        WebUiWorkbenchViews.RegisterBuiltIns(views, resolver);

        var registry = new UiContributionRegistry(
            new UiPointCatalog(WebUiPoints.All),
            [new WebUiUiContributionValidator(resolver)]);

        var module = new Seeing.Agent.Files.FileWorkspaceModule();
        registry.AddRange(module.Id, module.DescribeUi());

        registry.Diagnostics.Should().BeEmpty(
            "工作台视图键必须能在宿主视图表解析，否则启动期就被拒收");

        foreach (var contribution in registry.Get<Seeing.Agent.Abstractions.Ui.Workbench.WorkbenchContribution>(
                     Seeing.Agent.Abstractions.Ui.Workbench.WorkbenchPoints.DockId))
        {
            views.Resolve(contribution.ViewKey).Should().NotBeNull(
                $"工作台 '{contribution.Id}' 的视图键 '{contribution.ViewKey}' 必须在工作台视图表登记");
        }
    }

    [Fact]
    public void WorkbenchContribution_ShouldFollowModuleEnablementAndSessionGate()
    {
        var point = WebUiPoints.WorkbenchDock;
        var contribution = new Seeing.Agent.Files.FileWorkspaceModule().DescribeUi()
            .OfType<Seeing.Agent.Abstractions.Ui.Workbench.WorkbenchContribution>()
            .First(c => c.Id == "files");

        // 模块启用 + 不收窄 → 可见
        UiVisibility.IsVisible(contribution, point, new UiVisibilityContext(new Catalog("files.workspace"), null))
            .Should().BeTrue();

        // 模块停用 → 隐藏
        var disabled = UiVisibility.Evaluate(contribution, point, new UiVisibilityContext(new Catalog("memory"), null));
        disabled.Visible.Should().BeFalse();
        disabled.Reason.Should().Be(UiVisibilityReason.ModuleDisabled);

        // 会话工作模式收窄掉了文件模块 → 隐藏
        var narrowed = UiVisibility.Evaluate(
            contribution, point,
            new UiVisibilityContext(
                new Catalog("files.workspace", "memory"),
                new HashSet<string>(["memory"], StringComparer.OrdinalIgnoreCase)));
        narrowed.Visible.Should().BeFalse();
        narrowed.Reason.Should().Be(UiVisibilityReason.ScenarioMismatch);
    }

    [Fact]
    public void SettingsCard_ShouldIgnoreSessionGate()
    {
        // 侧栏/设置卡是 Process 作用域：会话工作模式不得收窄它们，否则切到窄场景会失去设置入口
        var point = WebUiPoints.ShellSettingsCard;
        var card = (SettingsCardContribution)BuildRegistry().All
            .Single(e => e.Contribution is SettingsCardContribution
                         && e.Contribution.Id == "settings.memory").Contribution;

        UiVisibility.IsVisible(
            card, point,
            new UiVisibilityContext(
                new Catalog("memory"),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            .Should().BeTrue("设置卡不随会话工作模式收窄");
    }

    [Fact]
    public void SlotHostAndSettings_ShouldWrapDynamicComponentInErrorBoundary()
    {
        ReadWebUiFile("Shared", "SlotHost.razor").Should().Contain("<ErrorBoundary");
        ReadWebUiFile("Pages", "Settings.razor").Should().Contain("<ErrorBoundary");
    }

    [Fact]
    public void Settings_ShouldKeyTabsByOwnerAndId()
    {
        // 新契约没有 Route，Tab key 必须由 OwnerId + Id 拼（见 RefreshCards）
        ReadWebUiFile("Pages", "Settings.razor").Should().Contain("{x.Entry.OwnerId}.{x.Card!.Id}");
    }

    [Fact]
    public void SourceMustNotReferenceLegacyContributionTypes()
    {
        // src/ 下不得再出现旧类型名（tests/probe 不在扫描范围内，且计划要求不动它）
        var root = FindRepositoryRoot();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f) is var text
                        && s_legacyTypeNames.Any(text.Contains))
            .ToList();

        offenders.Should().BeEmpty(
            "旧贡献形状已删除；残留引用说明还有通道没切到 UI 信封：" + string.Join(", ", offenders));
    }

    // ── helpers ──

    private static WebUiViewResolver ResolveViews()
    {
        var views = new WebUiViewResolver();
        WebUiViews.RegisterBuiltIns(views);
        WebUiWorkbenchViews.RegisterBuiltIns(new Seeing.Agent.WebUI.Workbench.WorkbenchViewRegistry(), views);
        return views;
    }

    private static UiContributionRegistry BuildRegistry()
    {
        var registry = new UiContributionRegistry(
            new UiPointCatalog(WebUiPoints.All),
            [new WebUiUiContributionValidator(ResolveViews())]);

        var host = new WebUiHostContributor();
        registry.AddRange(host.OwnerId, host.DescribeUi());

        // 工作台与页面都由模块自己声明（能力包只给 ViewKey，组件类型登记在宿主）
        var files = new Seeing.Agent.Files.FileWorkspaceModule();
        registry.AddRange(files.Id, files.DescribeUi());

        return registry;
    }

    private static string ReadWebUiFile(params string[] relativeSegments)
    {
        var parts = new List<string> { FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI" };
        parts.AddRange(relativeSegments);

        var path = Path.Combine(parts.ToArray());
        File.Exists(path).Should().BeTrue($"应找到源文件 {path}");

        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(CurrentFile())!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>模块启用集替身（本组测试只关心「启用/停用」，不关心依赖结算）。</summary>
    private sealed class Catalog : IModuleCatalog
    {
        private readonly HashSet<string> _enabled;

        public Catalog(params string[] enabled)
            => _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<ModuleDescriptor> Available => [];
        public IReadOnlyCollection<string> Enabled => _enabled;
        public IReadOnlyDictionary<string, string?> Unhealthy => new Dictionary<string, string?>();
        public bool IsEnabled(string id) => _enabled.Contains(id);
        public bool IsAvailable(string id) => true;
    }
}
