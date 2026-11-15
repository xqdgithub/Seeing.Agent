using FluentAssertions;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 输入框 placeholder 守门：移动端不得改用 HelpText（过长会撑高/换行）。
/// </summary>
public class PromptInputPlaceholderTests
{
    [Fact]
    public void Textarea_Should_Use_Placeholder_On_All_Viewports()
    {
        var source = ReadWebUiSource("Components/PromptInput.razor");

        source.Should().Contain("placeholder=\"@Placeholder\"",
            "移动端与 PC 共用 Placeholder；不得用 HelpText 当 placeholder");

        source.Should().NotContain("IsMobile ? HelpText",
            "移动端曾用 HelpText 作 placeholder，文案过长且与 PC 不一致");
    }

    [Fact]
    public void ThinkingSelect_Should_Be_Compact_And_Right_Aligned()
    {
        var css = ReadWebUiSource("wwwroot/css/prompt-input.css");

        css.Should().Contain(".prompt-thinking-select", "应有思考档位选择器样式");
        css.Should().MatchRegex(@"\.prompt-thinking-select\s*\{[^}]*width:\s*calc\(3\.5em \+ 12px\)",
            "固定约 3.5 字宽（比四字再收半个汉字），换档时不挤动旁边控件");
        css.Should().Contain("text-align: right",
            "选中文案应靠右贴近下拉箭头");
        css.Should().Contain("padding-inline-end: 2px",
            "文案与下拉箭头之间只留紧凑间距");
        css.Should().Contain("text-overflow: clip",
            "四字固定宽内不应用省略号");
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
