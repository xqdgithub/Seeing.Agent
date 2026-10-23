using FluentAssertions;
using Seeing.Agent.Tools.Support;
using Xunit;

namespace Seeing.Agent.Tools.Support.Tests.FilePaste;

public class PastedFileLayoutTests
{
    [Fact]
    public void SafeSessionId_ShouldKeepSafeCharactersAndReplaceOthers()
        => PastedFileLayout.SafeSessionId("a/b:c").Should().Be("a_b_c");

    [Fact]
    public void BuildStoredFileName_ShouldKeepReadableBaseAndExtension()
        => PastedFileLayout.BuildStoredFileName(
                "my report.pdf",
                new DateTimeOffset(2026, 10, 23, 15, 30, 12, TimeSpan.Zero),
                "a1b2")
            .Should().Be("my report-20261023-153012-a1b2.pdf");

    [Fact]
    public void TryGetProjectRelativePath_Inside_ShouldUseForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "seeing-layout");
        var file = Path.Combine(root, ".seeing", "uploads", "s1", "a.txt");

        PastedFileLayout.TryGetProjectRelativePath(root, file, out var relative).Should().BeTrue();

        relative.Should().Be(".seeing/uploads/s1/a.txt");
    }

    [Fact]
    public void ToPromptPath_Outside_ShouldReturnAbsolutePath()
    {
        var project = Path.Combine(Path.GetTempPath(), "p");
        var other = Path.Combine(Path.GetTempPath(), "o", "a.txt");

        PastedFileLayout.ToPromptPath(project, other)
            .Should().Be(other.Replace('\\', '/'));
    }

    [Fact]
    public void FormatPromptBlock_ShouldWrapEachPathInBackticks()
        => PastedFileLayout.FormatPromptBlock(["a.txt", "b/c.pdf"])
            .Should().Be("`a.txt`\n`b/c.pdf`");

    [Fact]
    public void AppendPromptPaths_ShouldAddExactlyOneNewline()
    {
        PastedFileLayout.AppendPromptPaths("hello", ["a.txt"])
            .Should().Be("hello\n`a.txt`");

        PastedFileLayout.AppendPromptPaths("", ["a.txt"])
            .Should().Be("`a.txt`");
    }
}
