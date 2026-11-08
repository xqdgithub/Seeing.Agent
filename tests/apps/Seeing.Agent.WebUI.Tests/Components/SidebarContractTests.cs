using FluentAssertions;
using Seeing.Agent.WebUI.Components;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

/// <summary>
/// 侧栏折叠契约守卫。
/// <para>
/// 这里钉住的都是「曾经出过 bug 的结构性约束」，不是实现细节：
/// </para>
/// <list type="number">
/// <item>折叠状态只有 AppState 一个来源，组件不得再持参数副本（否则多份状态会不同步）。</item>
/// <item>折叠态不再沿用默认展开分组。AntDesign 折叠时按 OpenKeys 把子菜单渲染成
/// body 上的浮层 —— 传默认值会让浮层在折叠瞬间自动弹出，既导致 hover 失效
/// （浮层已处于展开态），也会在展开后残留。</item>
/// <item>Menu / Dropdown 随折叠状态重建：Ant 只对 inline 子菜单 Close()，
/// 不隐藏已挂到 body 的浮层，重建是唯一不依赖其内部时序的清理方式。</item>
/// <item>侧栏宽度单一来源，CSS 不得再写字面量。</item>
/// </list>
/// </summary>
public class SidebarContractTests
{
    private static readonly string SourceFilePath = CurrentFile();

    private static string CurrentFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void Sidebar_Should_Read_Collapsed_State_From_AppState_Only()
    {
        var razor = ReadWebUiFile("Components", "AppSidebar.razor");

        razor.Should().NotContain("[Parameter] public bool Collapsed",
            "折叠态必须只有 AppState 一个来源，组件不得再持参数副本");

        razor.Should().NotContain("[Parameter] public EventCallback<bool> CollapsedChanged",
            "折叠态变更不应再经参数回调往返（会导致同一状态被写两次）");

        razor.Should().Contain("AppState.SidebarCollapsed",
            "折叠态应直接读写 AppState");
    }

    [Fact]
    public void Collapsed_Menu_Should_Not_Carry_Default_OpenKeys()
    {
        var razor = ReadWebUiFile("Components", "AppSidebar.razor");

        razor.Should().Contain("IsMenuCollapsed ? [] : _openKeys",
            "折叠态必须传空 OpenKeys：否则 Ant 会把默认分组渲染成浮层，"
            + "既让 hover 失效（浮层已展开），也会在切换后残留");
    }

    [Fact]
    public void Collapsed_State_Should_Not_Write_Back_OpenKeys()
    {
        var razor = ReadWebUiFile("Components", "AppSidebar.razor");

        // OnOpenKeysChanged 必须提前返回，否则浮层的瞬时展开状态会污染用户偏好
        razor.Should().Contain("if (IsMenuCollapsed)",
            "折叠态的展开/收起是浮层瞬时状态，不应回写为用户的展开偏好");
    }

    [Fact]
    public void Menu_And_Dropdown_Keys_Should_Depend_On_Collapsed_State()
    {
        ReadWebUiFile("Components", "AppSidebar.razor")
            .Should().Contain("@key=\"@(IsMenuCollapsed ?",
                "主菜单必须随折叠状态重建，否则折叠态浮层会残留到展开后");

        ReadWebUiFile("Components", "WorkspaceSwitcherButton.razor")
            .Should().Contain("@key=\"@(Collapsed ?",
                "工作区下拉必须随折叠状态重建，否则展开后下拉浮层会停在折叠时的位置");
    }

    [Fact]
    public void Sidebar_Width_Should_Have_Single_Source()
    {
        var mainLayoutCss = ReadWebUiFile("wwwroot", "css", "main-layout.css");

        mainLayoutCss.Should().Contain("margin-left: var(--sider-width)",
            "内容区左边距必须取自 CSS 变量，不能写死 240px");
        mainLayoutCss.Should().Contain("margin-left: var(--sider-collapsed-width)",
            "折叠态边距同样必须取自变量，不能写死 80px");

        // 宽度字面量只允许出现在 AppSidebar 常量里
        mainLayoutCss.Should().NotContain("margin-left: 240px");
        mainLayoutCss.Should().NotContain("margin-left: 80px");

        AppSidebar.ExpandedWidth.Should().Be(240);
        AppSidebar.CollapsedWidth.Should().Be(80);
    }

    [Fact]
    public void Collapsed_Workspace_Trigger_Should_Fill_Row()
    {
        var css = ReadWebUiFile("wwwroot", "css", "sidebar.css");

        css.Should().Contain(".ws-switcher .antblazor-overlay-trigger-wrapper",
            "Dropdown 触发器包裹层默认 inline-block + fit-content，不拉满则按钮居中无效、图标贴左边");

        css.Should().Contain("width: 100% !important",
            "包裹层必须拉满整行");
    }

    [Fact]
    public void Sidebar_Styles_Should_Use_DataTheme_Not_PrefersColorScheme()
    {
        foreach (var file in new[] { "sidebar.css", "main-layout.css" })
        {
            ReadWebUiFile("wwwroot", "css", file)
                .Should().NotContain("@media (prefers-color-scheme",
                    $"{file} 应使用全站统一的 [data-theme=\"dark\"]；"
                    + "混用 media query 会导致用户手动切主题时侧栏不跟随");
        }
    }

    [Fact]
    public void Sidebar_Menu_Icons_Should_Use_Icon_Parameter_Not_ChildContent()
    {
        var razor = ReadWebUiFile("Components", "AppSidebar.razor");

        // 折叠态 AntDesign 会用菜单项的 content 作 tooltip 内容。
        // 图标若写在 ChildContent 里，就会连同图标一起出现在 tooltip 中，
        // 形成「图标 + 图标 + 文字」的重复装饰（用户看到的重影）。
        // MenuItem.Icon 参数渲染在 content 之外，不会进入 tooltip。
        razor.Should().Contain("Icon=\"@IconType.Outline.Message\"",
            "普通菜单项的图标必须走 MenuItem.Icon 参数");
        razor.Should().Contain("Icon=\"@item.Icon\"",
            "动态菜单项的图标必须走 MenuItem.Icon 参数");
    }

    [Fact]
    public void Collapsed_Menu_Item_Icon_Should_Have_Centering_Rule()    {
        var css = ReadWebUiFile("wwwroot", "css", "sidebar.css");

        css.Should().Contain(".sider-menu .ant-menu-item > .anticon",
            "Icon 参数渲染的图标在 title-content 之外，折叠态需要独立居中规则，"
            + "否则图标会被推到左缘");

        css.Should().Contain(".sider-menu .ant-menu-item > .ant-menu-title-content",
            "折叠态下单图标菜单项的 title-content 必须让位，否则它会挤掉图标的居中空间");
    }

    [Fact]
    public void Badge_Should_Not_Be_In_MenuItem_ChildContent()
    {
        var razor = ReadWebUiFile("Components", "AppSidebar.razor");

        // 折叠态 tooltip 取 MenuItem 的 content（Title / ChildContent）。
        // Badge 若留在 ChildContent 里，会被复制进 tooltip，而 Ant 的 .ant-badge
        // 规则自带 color: rgba(0,0,0,0.85)，导致该 tooltip 文字变黑（其它项是白字）。
        // 正确做法是塞进 IconTemplate —— 它在 content 之外，不参与 tooltip。
        razor.Should().Contain("<IconTemplate>",
            "带角标的菜单项必须用 IconTemplate 承载 Badge，避免 Badge 被复制进 tooltip 改变文字颜色");

        razor.Should().NotContain("<Badge Count=\"@PermissionView.TotalCount\">\n                <span>",
            "Badge 不应直接包住菜单文字");
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
        var dir = new DirectoryInfo(Path.GetDirectoryName(SourceFilePath)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }
}
