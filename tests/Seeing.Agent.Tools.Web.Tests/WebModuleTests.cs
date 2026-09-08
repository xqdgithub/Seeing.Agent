using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Web.Tests;

public class WebModuleTests
{
    [Fact]
    public void Module_Id_IsWeb()
    {
        var module = new WebModule();
        module.Id.Should().Be("web");
    }

    [Fact]
    public void ProvidedTools_ContainsWebFetch()
    {
        var module = new WebModule();
        module.ProvidedTools.Should().Contain("webfetch");
    }

    [Fact]
    public void ProvidedTools_ContainsAllWebTools()
    {
        var module = new WebModule();
        module.ProvidedTools.Should().BeEquivalentTo(["webfetch", "websearch", "codesearch"]);
    }
}
