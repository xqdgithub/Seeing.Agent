using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Llm.TypeSafe.Tests;

public class ScaffoldSmokeTests
{
    [Fact]
    public void CapabilityAssembly_IsReferenced()
    {
        var assembly = Assembly.Load("Seeing.Agent.Llm.TypeSafe");

        assembly.GetName().Name.Should().Be("Seeing.Agent.Llm.TypeSafe");
    }
}
