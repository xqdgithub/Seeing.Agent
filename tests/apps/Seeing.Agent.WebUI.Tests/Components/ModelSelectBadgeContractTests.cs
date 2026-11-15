using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

/// <summary>
/// 模型 Badge 点击不得先闪 AntDesign Tooltip 再开 Modal（会挤一下页面，观感怪异）。
/// </summary>
public class ModelSelectBadgeContractTests
{
    private static readonly string SourceFilePath = CurrentFile();

    private static string CurrentFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void Badge_Should_Not_Wrap_Chip_In_AntDesign_Tooltip()
    {
        var razor = ReadWebUiFile("Components", "Models", "ModelSelectBadge.razor");

        razor.Should().NotContain("<Tooltip",
            "AntDesign Tooltip 在点击开 Modal 时会先弹出黑条并可能锁滚动挤页面；"
            + "完整 key 改用原生 title 悬停提示");

        razor.Should().Contain("title=\"@(_modalVisible ? null : _badgeTitle)\"",
            "弹窗打开期间应关掉原生 title，避免遮罩下还挂着悬停提示");
    }

    [Fact]
    public void Body_Should_Not_Shrink_When_Modal_Opens()
    {
        var css = ReadWebUiFile("wwwroot", "css", "design-system.css");
        var js = ReadWebUiFile("wwwroot", "js", "app.js");

        js.Should().Contain("neutralizeAntModalBodyScroll",
            "须空操作 AntDesign disableBodyScroll，根治 Modal 打开时整页闪缩");
        js.Should().Contain("helper.disableBodyScroll = function () { }",
            "disableBodyScroll 必须被替换为空函数");

        css.Should().Contain("body.ant-scrolling-effect",
            "CSS 兜底：即使仍打上 ant-scrolling-effect，也强制 100% 防闪缩");
        css.Should().Contain("width: 100% !important",
            "覆盖 Modal 的 inline width 收缩");
    }

    [Fact]
    public void Composer_Caret_Should_Not_Use_Button_After_Pseudo()
    {
        var razor = ReadWebUiFile("Components", "Models", "ModelSelectBadge.razor");
        var promptCss = ReadWebUiFile("wwwroot", "css", "prompt-input.css");

        razor.Should().Contain("model-select-badge-caret",
            "下拉三角须为独立 DOM，避免与 ant-btn 点击波纹抢 ::after");
        promptCss.Should().Contain(".model-select-badge-caret",
            "composer 变体应样式化独立 caret");
        promptCss.Should().MatchRegex(
            @"model-select-badge-chip\.ant-btn::after\s*\{[^}]*content:\s*none",
            "composer 按钮须禁用 ::after，防止点击闪出蓝色下箭头");
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
