using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Llm.SystemOne.Tests;

public class ScaffoldSmokeTests
{
    [Fact]
    public void CapabilityAssembly_IsReferenced()
    {
        var assembly = Assembly.Load("Seeing.Agent.Llm.SystemOne");

        assembly.GetName().Name.Should().Be("Seeing.Agent.Llm.SystemOne");
    }
}
