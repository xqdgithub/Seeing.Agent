using FluentAssertions;
using Seeing.Agent.WebUI.Files.Services;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileTreeStateTests
{
    [Fact]
    public void SetSelected_NewPath_ShouldUpdateAndRaise()
    {
        var s = new FileTreeState();
        var raised = 0;
        s.SelectionChanged += () => raised++;

        s.SetSelected("src/app/main.js");

        s.SelectedFile.Should().Be("src/app/main.js");
        raised.Should().Be(1);
        s.SelectionVersion.Should().Be(1);
    }

    [Fact]
    public void SetSelected_BackslashPath_ShouldNormalize()
    {
        var s = new FileTreeState();

        s.SetSelected("src\\app\\main.js");

        s.SelectedFile.Should().Be("src/app/main.js");
    }

    [Fact]
    public void SetSelected_SameValue_ShouldNotRaise()
    {
        var s = new FileTreeState();
        s.SetSelected("a.txt");
        var raised = 0;
        s.SelectionChanged += () => raised++;

        s.SetSelected("a.txt");

        raised.Should().Be(0);
    }

    [Fact]
    public void SetSelected_SameValueDifferentCase_ShouldNotRaise()
    {
        var s = new FileTreeState();
        s.SetSelected("A.txt");
        var raised = 0;
        s.SelectionChanged += () => raised++;

        s.SetSelected("a.txt");

        raised.Should().Be(0);
    }

    [Fact]
    public void SetSelected_Null_ShouldClearSelection()
    {
        var s = new FileTreeState();
        s.SetSelected("a.txt");

        s.SetSelected(null);

        s.SelectedFile.Should().BeNull();
        s.SelectionVersion.Should().Be(2);
    }
}
