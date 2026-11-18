using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

public class ComposerModelPickerActionContractTests
{
    [Fact]
    public void Composer_Should_Register_ModelPicker_Action()
    {
        var razor = ReadWebUiFile("Components", "ComposerModelPicker.razor");
        razor.Should().Contain("CommandActionNames.ModelPicker");
        razor.Should().Contain("OpenAsync");
        razor.Should().Contain("_registeredSessionId");
    }

    [Fact]
    public void Badge_Should_Expose_Public_Open()
    {
        ReadWebUiFile("Components", "Models", "ModelSelectBadge.razor")
            .Should().Contain("public async Task OpenAsync()");
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
