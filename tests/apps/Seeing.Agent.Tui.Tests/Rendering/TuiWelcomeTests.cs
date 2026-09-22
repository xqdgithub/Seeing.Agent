using System.Text;
using FluentAssertions;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

public sealed class TuiWelcomeTests
{
    // 系统控制台默认代码页 936(GBK)：起始页文案必须能在该编码下无损往返，否则渲染成 ?。
    private static readonly Encoding Gbk = CreateGbk();

    private static Encoding CreateGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    private static string Render(IRenderable renderable, int width = 120)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Profile.Height = 40;
        console.Write(renderable);
        return writer.ToString();
    }

    private static TuiViewState NewState(bool executing = false, params TuiBlock[] blocks)
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            AgentId = "build",
            IsExecuting = executing,
        };

        foreach (var block in blocks)
            state.Upsert(block);

        return state;
    }

    private static TuiBlock Block(TuiBlockKind kind, string key = "k1")
        => new() { Key = key, Kind = kind, Text = "x" };

    [Fact]
    public void IsStartPage_WithoutBlocks_ShouldBeTrue()
    {
        TuiWelcome.IsStartPage(NewState()).Should().BeTrue();
    }

    [Fact]
    public void IsStartPage_WithOnlySystemBlock_ShouldBeTrue()
    {
        TuiWelcome.IsStartPage(NewState(blocks: Block(TuiBlockKind.System))).Should().BeTrue();
    }

    [Theory]
    [InlineData(TuiBlockKind.User)]
    [InlineData(TuiBlockKind.Assistant)]
    [InlineData(TuiBlockKind.Tool)]
    [InlineData(TuiBlockKind.Error)]
    [InlineData(TuiBlockKind.Compaction)]
    public void IsStartPage_WithConversationBlock_ShouldBeFalse(TuiBlockKind kind)
    {
        TuiWelcome.IsStartPage(NewState(blocks: Block(kind))).Should().BeFalse();
    }

    [Fact]
    public void IsStartPage_WhileExecuting_ShouldBeFalse()
    {
        TuiWelcome.IsStartPage(NewState(executing: true)).Should().BeFalse();
    }

    [Fact]
    public void Render_Wide_ShouldEmitEveryBannerLine()
    {
        var text = Render(TuiWelcome.Render(120));

        foreach (var line in TuiWelcome.WideBanner)
            text.Should().Contain(line);
    }

    [Fact]
    public void Render_Narrow_ShouldFallBackToSingleLineLogo()
    {
        var text = Render(TuiWelcome.Render(TuiWelcome.WideBannerMinWidth - 1), TuiWelcome.WideBannerMinWidth - 1);

        text.Should().Contain(TuiWelcome.NarrowLogo);
        text.Should().NotContain(TuiWelcome.WideBanner[0]);
    }

    [Fact]
    public void Render_ShouldEmitEveryTip()
    {
        var text = Render(TuiWelcome.Render(120));

        text.Should().Contain("可用操作");
        foreach (var (key, description) in TuiWelcome.Tips)
            text.Should().Contain(description);
    }

    [Fact]
    public void Render_Narrow_ShouldUseCompactTips()
    {
        var width = TuiWelcome.WideBannerMinWidth - 1;
        var text = Render(TuiWelcome.Render(width), width);

        foreach (var (key, description) in TuiWelcome.NarrowTips)
            text.Should().Contain(description);

        // 窄终端不得出现会在双列宽下折行的长描述。
        text.Should().NotContain("补全斜杠命令（输入 / 显示候选）");
    }

    [Fact]
    public void NarrowTipsRows_ShouldFitInNarrowWidth()
    {
        // 中文按 2 列宽计：键列 + 2 空格 + 描述 ≤ 宽度下限，保证不折行。
        var keyWidth = TuiWelcome.NarrowTips.Max(tip => tip.Key.Length);
        foreach (var (_, description) in TuiWelcome.NarrowTips)
        {
            var columns = keyWidth + 2 + description.Length * 2;
            columns.Should().BeLessThanOrEqualTo(TuiWelcome.WideBannerMinWidth - 1);
        }
    }

    [Fact]
    public void BannerLines_ShouldFitWideThreshold()
    {
        TuiWelcome.WideBanner.Max(line => line.Length)
            .Should().BeLessThanOrEqualTo(TuiWelcome.WideBannerMinWidth);
    }

    [Fact]
    public void BannerLines_ShouldNotEndWithWhitespace()
    {
        // 行尾空白会被编辑器裁剪并破坏大字对齐，故一律不留尾随空格。
        foreach (var line in TuiWelcome.WideBanner)
            line.Should().Be(line.TrimEnd());
    }

    [Fact]
    public void Render_ShouldNotUseNearInvisibleForegrounds()
    {
        // 实测：grey = ANSI 8（暗灰，黑底偏暗）、grey11 = xterm 234(#1c1c1c)（黑底等同不可见）。
        // 起始页只用终端默认前景色与亮色，故输出中不得出现这两个转义。
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 120;
        console.Profile.Height = 40;
        console.Write(TuiWelcome.Render(120));

        var raw = writer.ToString();
        raw.Should().NotContain("38;5;234");
        raw.Should().NotContain("38;5;8m");
    }

    [Fact]
    public void AllWelcomeText_ShouldRoundTripUnderGbk()
    {
        var texts = TuiWelcome.WideBanner
            .Append(TuiWelcome.NarrowLogo)
            .Concat(TuiWelcome.Tips.SelectMany(t => new[] { t.Key, t.Description }))
            .Concat(TuiWelcome.NarrowTips.SelectMany(t => new[] { t.Key, t.Description }));

        foreach (var value in texts)
            Gbk.GetString(Gbk.GetBytes(value)).Should().Be(value);
    }
}
