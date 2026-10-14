using FluentAssertions;
using Seeing.Agent.Tools.Support;
using Xunit;

namespace Seeing.Agent.Tools.Support.Tests;

public class LineDiffTests
{
    [Fact]
    public void Identical_ShouldReturnEmpty()
    {
        LineDiff.ComputeUnified("a\nb\n", "a\nb\n").Should().BeEmpty();
    }

    [Fact]
    public void SingleLineChange_ShouldHaveHunkHeader()
    {
        var diff = LineDiff.ComputeUnified("a\nb\nc\n", "a\nB\nc\n");
        diff.Should().Contain("@@");
        diff.Should().Contain("-b");
        diff.Should().Contain("+B");
    }

    [Fact]
    public void InsertedLine_ShouldNotMarkFollowingLinesChanged()
    {
        var diff = LineDiff.ComputeUnified("a\nc\n", "a\nb\nc\n");
        diff.Should().Contain("+b");
        diff.Should().NotContain("-c");
    }
}
