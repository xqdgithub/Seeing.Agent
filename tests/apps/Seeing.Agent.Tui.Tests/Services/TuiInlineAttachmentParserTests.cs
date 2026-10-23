using FluentAssertions;
using Seeing.Agent.Tui.Services;
using Xunit;

namespace Seeing.Agent.Tui.Tests.Services;

public class TuiInlineAttachmentParserTests
{
    [Fact]
    public void ParseInline_NonImage_ShouldMovePathOutOfText()
    {
        var plan = TuiInlineAttachmentParser.ParseInline(
            new TuiAttachmentResolver(),
            @"@C:\work\a.txt hello",
            @"C:\work");

        plan.RemainingText.Should().Be("hello");
        plan.PromptPaths.Should().Equal("a.txt");
        plan.ImagePaths.Should().BeEmpty();
    }

    [Fact]
    public void ParseInline_Image_ShouldKeepItAsImagePath()
    {
        var plan = TuiInlineAttachmentParser.ParseInline(
            new TuiAttachmentResolver(),
            @"@C:\work\photo.png hi",
            @"C:\work");

        plan.RemainingText.Should().Be("hi");
        plan.ImagePaths.Should().Equal(@"C:\work\photo.png");
        plan.PromptPaths.Should().BeEmpty();
    }

    [Fact]
    public void Classify_MixedPaths_ShouldSplitImageAndPromptPaths()
    {
        var plan = TuiInlineAttachmentParser.Classify(
            new TuiAttachmentResolver(),
            [@"C:\work\photo.png", @"C:\work\a.txt"],
            @"C:\work");

        plan.ImagePaths.Should().Equal(@"C:\work\photo.png");
        plan.PromptPaths.Should().Equal("a.txt");
    }
}
