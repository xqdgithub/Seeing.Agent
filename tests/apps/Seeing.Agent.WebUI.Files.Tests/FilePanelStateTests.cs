using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FilePanelStateTests
{
    private static FileResourceId Id(string rel) => new("root", rel);

    [Fact]
    public void Toggle_SameSession_ShouldClose()
    {
        var s = new FilePanelState();
        s.Toggle("s1");
        s.IsOpen.Should().BeTrue();

        s.Toggle("s1");
        s.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void RequestOpen_FromClosed_ShouldOpenAndHideTree()
    {
        var s = new FilePanelState();

        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")));

        s.IsOpen.Should().BeTrue();
        s.SessionId.Should().Be("s1");
        s.TreeVisible.Should().BeFalse();
        s.PendingOpen.Should().Be(new FileOpenRequest(Id("a.txt")));
    }

    [Fact]
    public void RequestOpen_WithPosition_ShouldCarryLineColumn()
    {
        var s = new FilePanelState();

        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt"), 10, 5));

        s.PendingOpen.Should().Be(new FileOpenRequest(Id("a.txt"), 10, 5));
    }

    [Fact]
    public void RequestOpen_WhenAlreadyOpen_ShouldPreserveTreeVisibility()
    {
        var s = new FilePanelState();
        s.Open("s1");
        s.SetTreeVisible(true);

        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")));

        s.TreeVisible.Should().BeTrue();
    }

    [Fact]
    public void ConsumePendingOpen_ShouldClearOnce()
    {
        var s = new FilePanelState();
        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")));

        s.ConsumePendingOpen().Should().Be(new FileOpenRequest(Id("a.txt")));
        s.ConsumePendingOpen().Should().BeNull();
    }

    [Fact]
    public void Close_ShouldClearPendingOpen()
    {
        var s = new FilePanelState();
        s.RequestOpen("s1", new FileOpenRequest(Id("a.txt")));

        s.Close();

        s.IsOpen.Should().BeFalse();
        s.PendingOpen.Should().BeNull();
    }

    [Fact]
    public void GetTabs_SameSession_ShouldReturnSameInstance()
    {
        var s = new FilePanelState();

        var a = s.GetTabs("s1");
        var b = s.GetTabs("s1");

        a.Should().BeSameAs(b);
    }

    [Fact]
    public void GetTabs_DifferentSessions_ShouldIsolate()
    {
        var s = new FilePanelState();

        var a = s.GetTabs("s1");
        var b = s.GetTabs("s2");

        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void SetTreeVisible_Changed_ShouldRaiseEventOnce()
    {
        var s = new FilePanelState();
        var count = 0;
        s.Changed += () => count++;

        s.SetTreeVisible(false);
        s.SetTreeVisible(false);

        count.Should().Be(1);
    }
}
