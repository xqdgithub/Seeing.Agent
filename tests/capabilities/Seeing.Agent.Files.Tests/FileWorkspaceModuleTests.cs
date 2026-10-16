using FluentAssertions;
using Seeing.Agent.Files.Hosting;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWorkspaceModuleTests
{
    [Fact]
    public void Module_ShouldBeConstructible_AndDeclareId()
    {
        var m = new FileWorkspaceModule();

        m.Id.Should().Be("files.workspace");
        m.DependsOn.Should().Contain("io.local");
        m.ProvidedSeams.Should().BeEmpty();
        m.ProvidedTools.Should().BeEmpty();
    }

    [Fact]
    public void HostedService_ShouldDeclareMatchingModuleId()
    {
        FileWatcherHostedService.ModuleIdValue.Should().Be("files.workspace");
        FileWatcherHostedService.ModuleIdValue.Should().Be(new FileWorkspaceModule().Id);
    }
}
