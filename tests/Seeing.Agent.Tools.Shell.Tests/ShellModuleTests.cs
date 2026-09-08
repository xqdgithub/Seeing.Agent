using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Shell.Tests;

public class ShellModuleTests
{
    [Fact]
    public void Module_Id_IsShell()
    {
        var module = new ShellModule();
        module.Id.Should().Be("shell");
    }

    [Fact]
    public void ProvidedTools_ContainsBash()
    {
        var module = new ShellModule();
        module.ProvidedTools.Should().Contain("bash");
    }

    [Fact]
    public void DependsOn_IncludesIoLocal()
    {
        var module = new ShellModule();
        module.DependsOn.Should().Contain("io.local");
    }
}
