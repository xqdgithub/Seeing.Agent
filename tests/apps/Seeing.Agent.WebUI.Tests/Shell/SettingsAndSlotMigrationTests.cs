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
    public void AllFourSlotPoints_ShouldExistAndBeSessionScoped()
    {
        (string SlotName, string PointId)[] expected =
        [
            ("session_prompt", "session.prompt"),
            ("session_toolbar_actions", "session.toolbar.actions"),
            ("message_toolbar", "session.input.toolbar"),
            ("session_file_panel", "session.file.panel"),
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
    public void FilePanelSlot_ShouldBeOwnedByFilesWorkspaceModule()
    {
        var registry = BuildRegistry();

        var entry = registry.All.Should()
            .ContainSingle(e => e.Contribution.Point == FilePanelUiContribution.PointId).Subject;

        entry.OwnerId.Should().Be(FilePanelUiContribution.OwnerModuleId,
            "文件面板的归属是能力模块 files.workspace，不是宿主");
        entry.Contribution.Requires.Should().Equal("files.workspace");

        var slot = (SlotContribution)entry.Contribution;
        ResolveViews().CanResolve(slot.ViewKey).Should().BeTrue(
            "文件面板视图键必须在宿主视图表登记（SessionFilePanel）");
    }

    [Fact]
    public void FilePanelSlot_ShouldFollowModuleEnablementAndSessionGate()
    {
        var point = WebUiPoints.SessionFilePanel;
        var slot = (SlotContribution)BuildRegistry().All
            .Single(e => e.Contribution.Point == point.Id).Contribution;

        // 模块启用 + 不收窄 → 可见
        UiVisibility.IsVisible(slot, point, new UiVisibilityContext(new Catalog("files.workspace"), null))
            .Should().BeTrue();

        // 模块停用 → 隐藏（面板消失，不靠撤销声明）
        var disabled = UiVisibility.Evaluate(slot, point, new UiVisibilityContext(new Catalog("memory"), null));
        disabled.Visible.Should().BeFalse();
        disabled.Reason.Should().Be(UiVisibilityReason.ModuleDisabled);

        // 会话工作模式收窄掉了文件模块 → 隐藏
        var narrowed = UiVisibility.Evaluate(
            slot, point,
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
        return views;
    }

    private static UiContributionRegistry BuildRegistry()
    {
        var registry = new UiContributionRegistry(
            new UiPointCatalog(WebUiPoints.All),
            [new WebUiUiContributionValidator(ResolveViews())]);

        var host = new WebUiHostContributor();
        registry.AddRange(host.OwnerId, host.DescribeUi());

        // 文件面板：归属能力模块，声明在宿主侧（组件类型不可下沉到能力包）
        registry.AddRange(FilePanelUiContribution.OwnerModuleId, new FilePanelUiContribution().DescribeUi());

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
