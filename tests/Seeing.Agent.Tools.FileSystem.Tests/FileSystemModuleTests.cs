using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.FileSystem.Tests;

public class FileSystemModuleTests
{
    [Fact]
    public void Module_Id_IsFilesystem()
    {
        var module = new FileSystemModule();
        module.Id.Should().Be("filesystem");
    }

    [Fact]
    public void ProvidedTools_ContainsRead()
    {
        var module = new FileSystemModule();
        module.ProvidedTools.Should().Contain("read");
    }

    [Fact]
    public void DependsOn_IncludesIoLocal()
    {
        var module = new FileSystemModule();
        module.DependsOn.Should().Contain("io.local");
    }
}
