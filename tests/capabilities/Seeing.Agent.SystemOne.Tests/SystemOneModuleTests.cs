using FluentAssertions;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneModuleTests
{
    [Fact]
    public void 模块元数据_应声明稳定Id且无工具与依赖()
    {
        var module = new SystemOneModule();

        module.Id.Should().Be("systemone");
        module.ProvidedTools.Should().BeEmpty();
        module.ProvidedSeams.Should().BeEmpty();
        module.DependsOn.Should().BeEmpty();
    }
}
