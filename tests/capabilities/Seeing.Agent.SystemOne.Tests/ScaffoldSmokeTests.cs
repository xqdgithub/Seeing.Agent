using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class ScaffoldSmokeTests
{
    [Fact]
    public void CapabilityAssembly_IsReferenced()
    {
        var assembly = Assembly.Load("Seeing.Agent.SystemOne");

        assembly.GetName().Name.Should().Be("Seeing.Agent.SystemOne");
    }
}
