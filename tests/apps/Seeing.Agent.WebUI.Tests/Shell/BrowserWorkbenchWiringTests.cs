using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Core.Ui;
using Seeing.Agent.Tools.Browser;
using Seeing.Agent.Tools.Browser.Tools;
using Seeing.Agent.WebUI.Shell;
using Seeing.Agent.WebUI.Workbench;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Shell;

/// <summary>
/// 浏览器工作台的宿主接线守门。
/// <para>
/// 这三件事漏一件的症状都是「工作台点开什么也不出现」或「agent 列不出浏览器」，
/// 且编译器不会报错（<c>ViewKey</c> 是字符串）：
/// ① 组件必须登记到工作台视图表；② 组件必须声明 <c>WorkbenchViewContext</c> 参数；
/// ③ 声明必须挂在 <c>workbench.dock</c> 上且归属浏览器模块。
/// </para>
/// </summary>
public class BrowserWorkbenchWiringTests
{
    [Fact]
    public void BrowserViewKey_ShouldResolveInWorkbenchViewRegistry()
    {
        var views = new WorkbenchViewRegistry();
        var resolver = new WebUiViewResolver();

        WebUiWorkbenchViews.RegisterBuiltIns(views, resolver);

        views.Resolve(WorkbenchPoints.ViewKeyFor("browser")).Should().NotBeNull(
            "缺少登记时工作台会渲染成空容器，且不报错");
        resolver.Resolve(WorkbenchPoints.ViewKeyFor("browser")).Should().NotBeNull(
            "宿主通用视图表也要能解析，否则信封校验会拒收该贡献");
    }

    [Fact]
    public void BrowserContribution_ShouldPassEnvelopeValidation()
    {
        var views = new WebUiViewResolver();
        WebUiViews.RegisterBuiltIns(views);
        WebUiWorkbenchViews.RegisterBuiltIns(new WorkbenchViewRegistry(), views);

        var registry = new UiContributionRegistry(
            new UiPointCatalog(WebUiPoints.All),
            [new WebUiUiContributionValidator(views)]);

        var module = new BrowserModule();
        registry.AddRange(module.Id, module.DescribeUi());

        registry.Diagnostics.Should().BeEmpty(
            "视图键无法解析时会在这里被判为未装配并静默拒收");
    }

    [Fact]
    public void BrowserModule_ShouldDeclareBrowserWorkbench()
    {
        var contributions = new BrowserModule().DescribeUi()
            .OfType<WorkbenchContribution>()
            .ToArray();

        contributions.Should().ContainSingle();

        var browser = contributions[0];
        browser.Id.Should().Be("browser");
        browser.Point.Should().Be(WorkbenchPoints.DockId);
        browser.Requires.Should().Equal(["browser"], "归属必须是提供它的模块");
        browser.ViewKey.Should().Be(WorkbenchPoints.ViewKeyFor("browser"));
        browser.ContributesContext.Should().BeTrue("「用户正在浏览哪个网页」对 agent 有用");
        browser.ProvidedTools.Should().Contain("browser_open").And.Contain("browser_snapshot");
        browser.OpenPayloadSchema.Should().NotBeNull();
    }

    [Fact]
    public void BrowserContribution_Visibility_ShouldFollowModuleEnablement()
    {
        var point = WebUiPoints.WorkbenchDock;
        var contribution = new BrowserModule().DescribeUi()
            .OfType<WorkbenchContribution>()
            .Single();

        var enabled = new UiVisibilityContext(new Catalog("browser", "io.local"), null);
        UiVisibility.IsVisible(contribution, point, enabled).Should().BeTrue();

        var disabled = UiVisibility.Evaluate(contribution, point, new UiVisibilityContext(new Catalog("io.local"), null));
        disabled.Visible.Should().BeFalse();
        disabled.Reason.Should().Be(UiVisibilityReason.ModuleDisabled);
    }

    [Fact]
    public void BrowserModule_ShouldNotDeclareFakeDependency()
    {
        // 曾有 DependsOn = ["io.local"] 而全包无 IFileSystem 引用（探测用静态 File.Exists）。
        // 假依赖会让最小宿主的激活顺序与失败模式失真，必须为空。
        new BrowserModule().DependsOn.Should().BeEmpty(
            "浏览器可执行文件探测用静态 File.Exists，不经 IFileSystem / 执行世界");
    }

    [Fact]
    public void BrowserModule_ProvidedTools_ShouldMatchToolTypes()
    {
        var module = new BrowserModule();
        var toolTypes = typeof(BrowserOpenTool).Assembly
            .GetTypes()
            .Where(t => typeof(Seeing.Agent.Abstractions.Tools.ITool).IsAssignableFrom(t)
                        && !t.IsAbstract && !t.IsInterface)
            .Select(t => t.Namespace)
            .Where(ns => ns == typeof(BrowserOpenTool).Namespace)
            .ToArray();

        // 12 个具体工具类型（不含基类）
        toolTypes.Should().HaveCount(12);
        module.ProvidedTools.Should().HaveCount(12);
    }

    [Fact]
    public void StreamEndpoint_ShouldBeDisabledWhenModuleDisabled()
    {
        // 端点门控：模块停用时 HTTP 面也必须不可达（否则停用只隐藏了工具与工作台，
        // Chromium 仍能被连上并按需拉起，破坏「模块停用即能力消失」）。
        var disabled = new Catalog("io.local");
        Seeing.Agent.WebUI.Endpoints.BrowserStreamEndpoints.IsModuleEnabled(disabled).Should().BeFalse();

        var enabled = new Catalog("browser");
        Seeing.Agent.WebUI.Endpoints.BrowserStreamEndpoints.IsModuleEnabled(enabled).Should().BeTrue();

        // 无模块目录（极端宿主）按「不可用」处理，不放行
        Seeing.Agent.WebUI.Endpoints.BrowserStreamEndpoints.IsModuleEnabled(null).Should().BeFalse();
    }

    [Fact]
    public void StreamEndpoint_ModuleId_ShouldMatchBrowserModule()
    {
        Seeing.Agent.WebUI.Endpoints.BrowserStreamEndpoints.ModuleId
            .Should().Be(new BrowserModule().Id);
    }

    private sealed class Catalog : Seeing.Agent.Abstractions.Modules.IModuleCatalog
    {
        private readonly HashSet<string> _enabled;

        public Catalog(params string[] enabled) => _enabled = new HashSet<string>(enabled, StringComparer.Ordinal);

        public IReadOnlyCollection<Seeing.Agent.Abstractions.Modules.ModuleDescriptor> Available => [];

        public IReadOnlyCollection<string> Enabled => _enabled;

        public IReadOnlyDictionary<string, string?> Unhealthy => new Dictionary<string, string?>();

        public bool IsEnabled(string moduleId) => _enabled.Contains(moduleId);

        public bool IsAvailable(string moduleId) => true;
    }
}
