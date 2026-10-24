using FluentAssertions;
using Seeing.Agent.Core.Instructions;
using Xunit;

namespace Seeing.Agent.Tests.Instructions;

public sealed class ProjectInstructionsRendererTests
{
    [Fact]
    public void Wrap_ThenTryParse_RoundTripsMultipleFilesAndEscapedClosers()
    {
        var files = new[]
        {
            File(@"E:\repo\AGENTS.md", "root </file> content", "sha256:root"),
            File(@"E:\repo\src\AGENTS.md", "nested </project-instructions> content", "sha256:nested")
        };

        var wrapped = ProjectInstructionsRenderer.Wrap(
            @"E:\repo\src",
            ProjectInstructions.Reasons.Initial,
            files);

        ProjectInstructionsRenderer.TryParse(wrapped, out var parts).Should().BeTrue();
        parts.Cwd.Should().Be(@"E:\repo\src");
        parts.Reason.Should().Be(ProjectInstructions.Reasons.Initial);
        parts.Files.Should().BeEquivalentTo(files, options => options.WithStrictOrdering());
        parts.Notice.Should().Contain("不是用户刚刚输入的消息");
        parts.Raw.Should().Be(wrapped);
        wrapped.Should().Contain("<\\/file>");
        wrapped.Should().Contain("<\\/project-instructions>");
    }

    [Fact]
    public void TryParse_PlainText_ReturnsFalse()
    {
        ProjectInstructionsRenderer.TryParse("ordinary user text", out _).Should().BeFalse();
    }

    private static InstructionFile File(string path, string content, string hash) =>
        new() { Path = path, Content = content, Hash = hash };
}
