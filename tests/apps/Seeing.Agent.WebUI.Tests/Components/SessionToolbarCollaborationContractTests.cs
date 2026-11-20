using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

public class SessionToolbarCollaborationContractTests
{
    [Fact]
    public void Toolbar_ShouldHostCollaborationDirectoryEntry()
    {
        var razor = ReadWebUiFile("Components", "SessionToolbar.razor");

        razor.Should().Contain("_collaborationVisible");
        razor.Should().Contain("OpenCollaborationDirectories");
        razor.Should().Contain("CollaborationDirectoriesModal");
    }

    [Fact]
    public void Modal_ShouldUseWritePortAndShowPaths()
    {
        var razor = ReadWebUiFile("Components", "CollaborationDirectoriesModal.razor");

        razor.Should().Contain("ICollaborationDirectories");
        razor.Should().Contain("AddAsync");
        razor.Should().Contain("RemoveAsync");
        razor.Should().Contain("不可用");
    }

    private static string ReadWebUiFile(params string[] relativeSegments)
    {
        var parts = new List<string> { RepoRoot(), "samples", "Seeing.Agent.WebUI" };
        parts.AddRange(relativeSegments);
        var path = Path.Combine(parts.ToArray());
        File.Exists(path).Should().BeTrue($"应找到源文件 {path}");
        return File.ReadAllText(path);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));
}
