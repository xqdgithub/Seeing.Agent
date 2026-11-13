using FluentAssertions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Hooks.Directory;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookPointNamesTests
{
    [Fact]
    public void Resolve_KnownPoint_UsesRegistrySpec()
    {
        var spec = HookPointNames.Resolve("tool.execute.before");
        spec.Should().Be(HookRegistry.ToolExecuteBefore);
        HookPointNames.Resolve("llm.message").Policy.Should().Be(HookPolicy.Parallel);
    }

    [Fact]
    public void Resolve_UnknownPoint_UsesBlockingPlaceholder()
    {
        var spec = HookPointNames.Resolve("no.such.point");
        spec.Point.Should().Be("no.such.point");
        spec.Policy.Should().Be(HookPolicy.Blocking);
    }

    [Theory]
    [InlineData("chat.params")]
    [InlineData("Chat.Params")]
    [InlineData("CHAT.ON_ERROR")]
    public void IsLegacy_IgnoresCase(string point)
    {
        HookPointNames.IsLegacy(point).Should().BeTrue();
    }

    [Fact]
    public void IsLegacy_NewName_IsFalse()
    {
        HookPointNames.IsLegacy("llm.params").Should().BeFalse();
    }
}
