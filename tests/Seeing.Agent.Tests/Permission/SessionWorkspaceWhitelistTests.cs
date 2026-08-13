using FluentAssertions;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class SessionWorkspaceWhitelistTests
{
    private readonly SessionWorkspaceWhitelist _whitelist = new();

    [Fact]
    public void Contains_ExactDirectory_ShouldReturnTrue()
    {
        _whitelist.Add("s1", @"C:\data");
        _whitelist.Contains("s1", @"C:\data").Should().BeTrue();
    }

    [Fact]
    public void Contains_FileInDirectory_ShouldReturnTrue()
    {
        _whitelist.Add("s1", @"C:\data");
        _whitelist.Contains("s1", @"C:\data\sub\file.txt").Should().BeTrue();
    }

    [Fact]
    public void Contains_OutsideDirectory_ShouldReturnFalse()
    {
        _whitelist.Add("s1", @"C:\data");
        _whitelist.Contains("s1", @"C:\other\file.txt").Should().BeFalse();
    }

    [Fact]
    public void Contains_DifferentSession_ShouldReturnFalse()
    {
        _whitelist.Add("s1", @"C:\data");
        _whitelist.Contains("s2", @"C:\data\file.txt").Should().BeFalse();
    }

    [Fact]
    public void ClearSession_ShouldForgetAll()
    {
        _whitelist.Add("s1", @"C:\data");
        _whitelist.ClearSession("s1");
        _whitelist.Contains("s1", @"C:\data").Should().BeFalse();
    }
}
