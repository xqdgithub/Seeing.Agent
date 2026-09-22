using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class ForegroundProcessRunnerTests
{
    [Fact]
    public void CreateStartInfo_ShouldInheritConsoleInsteadOfRedirecting()
    {
        var psi = ForegroundProcessRunner.CreateStartInfo(
            @"D:\bin\seeing-tui.dll",
            @"D:\work",
            new[] { "--boot", "minimal" },
            new Dictionary<string, string?> { ["SEEING_WORKSPACE_ROOT"] = @"D:\work" });

        psi.FileName.Should().Be("dotnet");
        psi.UseShellExecute.Should().BeFalse();
        psi.CreateNoWindow.Should().BeFalse();
        psi.RedirectStandardInput.Should().BeFalse();
        psi.RedirectStandardOutput.Should().BeFalse();
        psi.RedirectStandardError.Should().BeFalse();
        psi.WorkingDirectory.Should().Be(@"D:\work");
        psi.ArgumentList.Should().Equal(@"D:\bin\seeing-tui.dll", "--boot", "minimal");
        psi.Environment["SEEING_WORKSPACE_ROOT"].Should().Be(@"D:\work");
    }

    [Fact]
    public void CreateStartInfo_WithoutArgumentsOrEnvironment_ShouldStillWork()
    {
        var psi = ForegroundProcessRunner.CreateStartInfo(@"D:\bin\seeing-tui.dll", @"D:\work");

        psi.ArgumentList.Should().Equal(@"D:\bin\seeing-tui.dll");
        psi.WorkingDirectory.Should().Be(@"D:\work");
    }
}
