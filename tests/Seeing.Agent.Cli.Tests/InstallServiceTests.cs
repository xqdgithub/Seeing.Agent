using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class InstallServiceTests
{
    [Fact]
    public void ResolveGlobalDir_Windows_ShouldUseBin()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\alice" : "/root";
        var expected = Path.Combine(home, "bin");
        InstallService.ResolveGlobalDir(isWindows: true, home).Should().Be(expected);
    }

    [Fact]
    public void ResolveGlobalDir_Unix_ShouldUseDotLocalBin()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\alice" : "/home/alice";
        var expected = Path.Combine(home, ".local", "bin");
        InstallService.ResolveGlobalDir(isWindows: false, home).Should().Be(expected);
    }

    [Fact]
    public void IsPathPresent_WhenContainsDir_ShouldReturnTrue()
    {
        const string path = @"C:\a;C:\b;C:\Users\alice\bin";
        InstallService.IsPathPresent(path, @"C:\Users\alice\bin").Should().BeTrue();
    }

    [Fact]
    public void IsPathPresent_WhenMissingDir_ShouldReturnFalse()
    {
        const string path = @"C:\a;C:\b";
        InstallService.IsPathPresent(path, @"C:\Users\alice\bin").Should().BeFalse();
    }

    [Fact]
    public void IsPathPresent_WhenPathNull_ShouldReturnFalse()
    {
        InstallService.IsPathPresent(null, @"C:\bin").Should().BeFalse();
    }

    [Fact]
    public void AppendPath_ShouldAppendDirWithSeparator()
    {
        InstallService.AppendPath(@"C:\a;C:\b", @"C:\bin")
            .Should().Be(@"C:\a;C:\b;C:\bin");
    }

    [Fact]
    public void AppendPath_WhenExistingNull_ShouldReturnJustDir()
    {
        InstallService.AppendPath(null, "/home/u/.local/bin").Should().Be("/home/u/.local/bin");
    }

    [Fact]
    public void IsPathPresent_ShouldIgnoreTrailingSeparatorCase()
    {
        const string path = @"C:\a;C:\Users\alice\bin\";
        InstallService.IsPathPresent(path, @"C:\Users\alice\bin").Should().BeTrue();
    }

    [Fact]
    public void GetLinkPath_ShouldBeInsideGlobalDir()
    {
        InstallService.GetLinkPath(@"C:\Users\alice\bin").Should().Be(@"C:\Users\alice\bin\seeing-cli");
    }
}