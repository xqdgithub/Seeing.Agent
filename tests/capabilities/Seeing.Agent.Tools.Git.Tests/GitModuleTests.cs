using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Core.Tools.Git.Tests;

public class GitModuleTests
{
    [Fact]
    public void Module_Id_IsGit()
    {
        var module = new GitModule();
        module.Id.Should().Be("git");
    }

    [Fact]
    public void ProvidedTools_ContainsGitStatus()
    {
        var module = new GitModule();
        module.ProvidedTools.Should().Contain("git_status");
    }

    [Fact]
    public void DependsOn_IncludesIoLocal()
    {
        var module = new GitModule();
        module.DependsOn.Should().Contain("io.local");
    }
}
