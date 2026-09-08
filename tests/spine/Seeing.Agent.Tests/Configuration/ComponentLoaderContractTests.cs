using FluentAssertions;
using Seeing.Agent.Abstractions.Components;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ComponentLoaderContractTests
{
    [Fact]
    public void IComponentLoader_声明ReloadAsync()
    {
        var method = typeof(IComponentLoader).GetMethod("ReloadAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<ComponentLoadResult>));
        method.GetParameters().Length.Should().Be(3); // services, workspaceRoot, ct
    }
}
