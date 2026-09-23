using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class ScaffoldSmokeTests
{
    [Fact]
    public void CapabilityAssembly_IsReferenced()
    {
        var assembly = Assembly.Load("Seeing.Agent.Tools.SystemOne");

        assembly.GetName().Name.Should().Be("Seeing.Agent.Tools.SystemOne");
    }
}
