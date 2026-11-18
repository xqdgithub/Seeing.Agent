using FluentAssertions;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 命令面板结构守卫：分组标题、可选行 id、键盘滚动跟随、数据来自注入服务（不是旧快照参数）。
/// </summary>
public class CommandPaletteGroupingTests
{
    [Fact]
    public void Dropdown_Should_Render_Group_Headers_Before_Each_Group()
    {
        var source = ReadWebUiSource("Components/PromptInput.razor");

        source.Should().Contain("command-group-header",
            "面板按来源分组展示，组标题由同一扁平列表顺序插入");
        source.Should().Contain("@cmd.GroupTitle",
            "组标题来自命令视图模型（内置命令 / 技能 / 自定义命令 / 扩展）");
    }

    [Fact]
    public void Dropdown_Options_Should_Have_Stable_Ids_For_Scrolling()
    {
        var source = ReadWebUiSource("Components/PromptInput.razor");

        source.Should().Contain("-opt-",
            "每个可选项需要稳定 id，键盘移动后据此滚动到可视区");
        source.Should().Contain("scrollCommandOptionIntoView",
            "上下键移动选中项后必须调用滚动助手");
    }

    [Fact]
    public void Scroll_Helper_Should_Exist_In_AppJs_And_Not_Move_Visible_Items()
    {
        var source = ReadWebUiSource("wwwroot/js/app.js");

        source.Should().Contain("function scrollCommandOptionIntoView",
            "JSRuntime 调用的函数必须存在");
        source.Should().Contain("block: 'nearest'",
            "已可见的选中项不应被挪动（避免逐键抖动）");
    }

    [Fact]
    public void Commands_Should_Come_From_Injected_Service_Not_Stale_Parameter()
    {
        var source = ReadWebUiSource("Components/PromptInput.razor");

        source.Should().Contain("CommandListService.GetAvailableCommands",
            "面板打开时现取注册表，技能动态注册 / 工作区切换后无需刷新页面");
        source.Should().Contain("if (!wasOpen)",
            "仅在「关闭 → 打开」时拉取，连打字符不重复请求");
    }

    [Fact]
    public void Group_Header_Should_Have_Stylesheet_Rule()
    {
        var css = ReadWebUiSource("wwwroot/css/quick-command.css");

        css.Should().Contain(".command-group-header",
            "组标题的排版在样式表里，不在标记里堆内联样式");
    }

    private static string ReadWebUiSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "samples", "Seeing.Agent.WebUI", relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 WebUI 源文件：{relativePath}");
    }
}
