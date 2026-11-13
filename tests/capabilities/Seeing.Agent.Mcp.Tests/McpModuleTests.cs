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
    public void ProvidedTools_Contains_Manage_And_Resource_Tools()
    {
        var module = new McpModule();
        module.ProvidedTools.Should().Contain("mcp_list_servers");
        module.ProvidedTools.Should().Contain("mcp_add_server");
        module.ProvidedTools.Should().Contain("mcp_import_servers");
        module.ProvidedTools.Should().Contain("mcp_list_resources");
        module.ProvidedTools.Should().Contain("mcp_read_resource");
        module.ProvidedTools.Should().Contain("mcp_list_resource_templates");
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new McpModule();
        module.DependsOn.Should().BeEmpty();
    }
}
