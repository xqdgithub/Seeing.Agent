using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Support.Tests;

public class FileDiffMetadataTests
{
    [Fact]
    public void Build_SmallContent_ShouldIncludeFullContent()
    {
        var m = FileDiffMetadata.Build("/a.txt", "old", "new", "--- a\n+++ b\n-old\n+new");

        m[FileMetadataKeys.Path].Should().Be("/a.txt");
        m[FileMetadataKeys.OldContent].Should().Be("old");
        m[FileMetadataKeys.NewContent].Should().Be("new");
        m.Should().ContainKey(FileMetadataKeys.Diff);
    }

    [Fact]
    public void Build_LargeContent_ShouldOmitFullContentButKeepDiff()
    {
        var big = new string('x', FileMetadataKeys.DiffContentMaxBytes + 1);

        var m = FileDiffMetadata.Build("/a.txt", big, "new", "unified-diff");

        m.Should().NotContainKey(FileMetadataKeys.OldContent);
        m.Should().NotContainKey(FileMetadataKeys.NewContent);
        m[FileMetadataKeys.Diff].Should().Be("unified-diff");
    }
}
