using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class DirectoryBrowserTests
{
    [Fact]
    public void List_ShouldReturnOnlyDirectories_SortedByName()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "b"));
        Directory.CreateDirectory(Path.Combine(dir.Root, "A"));
        File.WriteAllText(Path.Combine(dir.Root, "file.txt"), "x");

        var listing = new DirectoryBrowser().List(dir.Root);

        listing.Error.Should().BeNull();
        listing.Directories.Select(d => d.Name).Should().Equal("A", "b");
    }

    [Fact]
    public void List_MissingPath_ShouldReturnError_WithoutThrowing()
    {
        var listing = new DirectoryBrowser().List(
            Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")));

        listing.Error.Should().NotBeNull();
        listing.Directories.Should().BeEmpty();
    }

    [Fact]
    public void CreateSubdirectory_ShouldCreateAndReturnListing()
    {
        using var dir = new TempDir();
        var browser = new DirectoryBrowser();
        browser.List(dir.Root);

        var listing = browser.CreateSubdirectory(dir.Root, "new-folder");

        listing.Error.Should().BeNull();
        Directory.Exists(Path.Combine(dir.Root, "new-folder")).Should().BeTrue();
        listing.Directories.Select(d => d.Name).Should().Contain("new-folder");
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("../escape")]
    [InlineData(@"a\b")]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateSubdirectory_InvalidName_ShouldReturnError(string name)
    {
        using var dir = new TempDir();
        var browser = new DirectoryBrowser();
        browser.List(dir.Root);

        var listing = browser.CreateSubdirectory(dir.Root, name);

        listing.Error.Should().NotBeNull();
        Directory.GetDirectories(dir.Root).Should().BeEmpty();
    }

    [Fact]
    public void CreateSubdirectory_OutsideListedParent_ShouldReturnError()
    {
        using var allowed = new TempDir();
        using var outside = new TempDir();
        var browser = new DirectoryBrowser();
        browser.List(allowed.Root);

        var listing = browser.CreateSubdirectory(outside.Root, "x");

        listing.Error.Should().NotBeNull("父目录必须是本次已列出的目录");
        Directory.GetDirectories(outside.Root).Should().BeEmpty();
    }

    [Fact]
    public void GetRoots_ShouldNotBeEmpty() =>
        new DirectoryBrowser().GetRoots().Should().NotBeEmpty();

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-dir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
