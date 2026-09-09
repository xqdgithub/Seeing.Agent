using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Modules;

public class BootOverrideSourceTests
{
    [Theory]
    [InlineData(new[] { "--boot", "secure" }, "secure")]
    [InlineData(new[] { "--boot=dev" }, "dev")]
    [InlineData(new[] { "--urls", "http://localhost", "--boot", "minimal" }, "minimal")]
    [InlineData(new[] { "--BOOT=full" }, "full")]
    public void ResolveFromArgs_ParsesBootOption(string[] args, string expected)
    {
        BootOverrideSource.ResolveFromArgs(args).Should().Be(expected);
    }

    [Fact]
    public void ResolveFromArgs_MissingValue_ReturnsNull()
    {
        BootOverrideSource.ResolveFromArgs(["--boot"]).Should().BeNull();
        BootOverrideSource.ResolveFromArgs(["--boot="]).Should().BeNull();
        BootOverrideSource.ResolveFromArgs(null).Should().BeNull();
    }

    [Fact]
    public void ApplyToServices_SetsBootOverrideOnExistingOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ProcessSettlementOptions { HostDefaultBoot = "minimal" });
        BootOverrideSource.ApplyToServices(services, ["--boot", "secure"]);

        var opts = services.BuildServiceProvider().GetRequiredService<ProcessSettlementOptions>();
        opts.BootOverride.Should().Be("secure");
        opts.HostDefaultBoot.Should().Be("minimal");
    }
}

public class BuiltInCapabilitySetsSecureDevTests
{
    [Fact]
    public void Secure_IsStarWithDisabledHighRiskModules()
    {
        var set = BuiltInCapabilitySets.SecureSet;
        set.Name.Should().Be("secure");
        set.Modules.Should().BeEquivalentTo(["*"]);
        set.Disabled.Should().BeEquivalentTo(["shell", "mcp", "acp", "gateway"]);
        BuiltInCapabilitySets.TryGet("secure").Should().BeSameAs(set);
        BuiltInCapabilitySets.All.Should().Contain(set);
    }

    [Fact]
    public void Dev_SharesFullModuleListReference()
    {
        ReferenceEquals(BuiltInCapabilitySets.Dev, BuiltInCapabilitySets.Full).Should().BeTrue();
        BuiltInCapabilitySets.DevSet.Modules.Should().BeSameAs(BuiltInCapabilitySets.Full);
        BuiltInCapabilitySets.TryGet("dev").Should().BeSameAs(BuiltInCapabilitySets.DevSet);
        BuiltInCapabilitySets.All.Should().Contain(BuiltInCapabilitySets.DevSet);
    }
}
