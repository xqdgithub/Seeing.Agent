using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Core.Instructions;
using Seeing.Session.Core;
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

    [Fact]
    public void CreateUserMessage_SetsUserRoleContentAndMetadata()
    {
        var files = new[]
        {
            File(@"E:\repo\AGENTS.md", "root", "sha256:root"),
            File(@"E:\repo\src\AGENTS.md", "src", "sha256:src")
        };

        var message = ProjectInstructionsRenderer.CreateUserMessage(
            @"E:\repo\src",
            ProjectInstructions.Reasons.CwdChange,
            files);

        message.Role.Should().Be(MessageRole.User);
        ProjectInstructionsRenderer.TryParse(message.Content, out _).Should().BeTrue();
        message.Metadata![ProjectInstructions.MetadataKeys.ProjectInstructions].Should().Be(true);
        message.Metadata[ProjectInstructions.MetadataKeys.Reason].Should().Be(ProjectInstructions.Reasons.CwdChange);
        message.Metadata[ProjectInstructions.MetadataKeys.Cwd].Should().Be(@"E:\repo\src");
        JsonSerializer.Deserialize<string[]>(
                (string)message.Metadata[ProjectInstructions.MetadataKeys.Paths])
            .Should().Equal(files.Select(file => file.Path));
    }

    private static InstructionFile File(string path, string content, string hash) =>
        new() { Path = path, Content = content, Hash = hash };
}
