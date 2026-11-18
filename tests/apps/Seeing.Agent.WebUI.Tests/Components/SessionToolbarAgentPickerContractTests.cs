using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

public class SessionToolbarAgentPickerContractTests
{
    [Fact]
    public void Toolbar_Should_Register_AgentPicker_And_Control_Open()
    {
        var razor = ReadWebUiFile("Components", "SessionToolbar.razor");
        razor.Should().Contain("CommandActionNames.AgentPicker");
        razor.Should().Contain("Open=\"_agentPickerOpen\"");
        razor.Should().Contain("_registeredSessionId");
    }

    [Fact]
    public void Agent_Select_Should_Not_Be_In_Else_Of_SubAgent_Badge()
    {
        ReadWebUiFile("Components", "SessionToolbar.razor")
            .Should().NotContain("else if (_optionsReady && Context.CurrentSession != null)");
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
