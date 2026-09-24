using FluentAssertions;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneToolsModuleTests
{
    [Fact]
    public void 元数据_应正确()
    {
        var module = new SystemOneToolsModule();

        module.Id.Should().Be("systemone.tools");
        module.ProvidedTools.Should().BeEquivalentTo(
            new[] { "systemone_ask", "systemone_noul", "systemone_choice", "systemone_score" });
        module.ProvidedSeams.Should().BeEmpty();
        module.DependsOn.Should().Equal("systemone");
    }
}
