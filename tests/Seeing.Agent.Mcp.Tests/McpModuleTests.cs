using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

public class McpModuleTests
{
    [Fact]
    public void Module_Id_IsMcp()
    {
        var module = new McpModule();
        module.Id.Should().Be("mcp");
    }

    [Fact]
    public void ProvidedSeams_ContainsMcp()
    {
        var module = new McpModule();
        module.ProvidedSeams.Should().Equal("mcp");
    }

    [Fact]
    public void ProvidedTools_IsEmpty()
    {
        var module = new McpModule();
        module.ProvidedTools.Should().BeEmpty();
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new McpModule();
        module.DependsOn.Should().BeEmpty();
    }
}
