using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Basic.Tests;

public class BasicModuleTests
{
    [Fact]
    public void Module_Id_IsBasic()
    {
        var module = new BasicModule();
        module.Id.Should().Be("basic");
    }

    [Fact]
    public void ProvidedTools_ContainsCurrentTime()
    {
        var module = new BasicModule();
        module.ProvidedTools.Should().Contain("current_time");
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new BasicModule();
        module.DependsOn.Should().BeEmpty();
    }
}
