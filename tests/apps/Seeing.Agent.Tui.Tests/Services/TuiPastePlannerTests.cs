using FluentAssertions;
using Seeing.Agent.Tui.Services;
using Xunit;

namespace Seeing.Agent.Tui.Tests.Services;

public class TuiPastePlannerTests
{
    [Fact]
    public void Plan_Files_ShouldWinAndSplitImagesFromPaths()
    {
        var plan = TuiPastePlanner.Plan(
            terminalText: "\"C:\\a.txt\"",
            files: [
                new ClipboardFile(@"C:\a.txt", "text/plain", false, 1),
                new ClipboardFile(@"C:\b.png", "image/png", true, 2)
            ],
            clipboardText: "ignored",
            image: null,
            projectRoot: @"C:\work");

        plan.ImageFilePaths.Should().Equal(@"C:\b.png");
        plan.PromptPaths.Should().Equal("C:/a.txt");
        plan.Text.Should().BeNull();
    }

    [Fact]
    public void Plan_NoFiles_ShouldPreferTerminalTextThenClipboardText()
    {
        TuiPastePlanner.Plan("pasted", [], "clip", null, @"C:\work").Text.Should().Be("pasted");
        TuiPastePlanner.Plan(null, [], "clip", null, @"C:\work").Text.Should().Be("clip");
    }

    [Fact]
    public void Plan_NoText_ShouldUseScreenshot()
    {
        var image = new ClipboardImage([1, 2, 3], "screenshot.png", 3);

        var plan = TuiPastePlanner.Plan(null, [], null, image, @"C:\work");

        plan.Screenshot.Should().BeSameAs(image);
    }
}
