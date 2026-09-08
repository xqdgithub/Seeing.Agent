using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ConfigSectionRegistryContractsTests
{
    [Fact]
    public void ConfigScope_Should_Define_Both_UserOnly_And_ProjectOnly()
    {
        Enum.GetNames(typeof(ConfigScope)).Should().Equal("Both", "UserOnly", "ProjectOnly");
    }

    [Fact]
    public void ConfigSectionMeta_Should_Be_Record_With_Expected_Positional_Parameters()
    {
        var metaType = typeof(ConfigSectionMeta);

        metaType.IsSealed.Should().BeTrue();
        metaType.GetMethod("<Clone>$").Should().NotBeNull();

        var ctor = metaType.GetConstructors().Single();
        var parameters = ctor.GetParameters().Select(p => (p.Name, p.ParameterType, p.HasDefaultValue)).ToList();

        parameters.Should().Equal([
            ("Key", typeof(string), false),
            ("FileName", typeof(string), false),
            ("Scope", typeof(ConfigScope), false),
            ("SectionType", typeof(Type), false),
            ("DefaultValue", typeof(object), true),
        ]);
    }

    [Fact]
    public void ConfigSectionMeta_DefaultValue_Should_Be_Optional()
    {
        var meta = new ConfigSectionMeta("Gateway", "seeing.json", ConfigScope.ProjectOnly, typeof(string));

        meta.Key.Should().Be("Gateway");
        meta.FileName.Should().Be("seeing.json");
        meta.Scope.Should().Be(ConfigScope.ProjectOnly);
        meta.SectionType.Should().Be(typeof(string));
        meta.DefaultValue.Should().BeNull();
    }

    [Fact]
    public void IConfigSectionRegistry_Should_Define_Register_Sections_And_TryGet()
    {
        var registryType = typeof(IConfigSectionRegistry);

        var register = registryType.GetMethod(nameof(IConfigSectionRegistry.Register));
        register.Should().NotBeNull();
        register!.GetParameters().Should().ContainSingle(p => p.ParameterType == typeof(ConfigSectionMeta));

        registryType.GetProperty(nameof(IConfigSectionRegistry.Sections))!.PropertyType
            .Should().Be(typeof(IReadOnlyCollection<ConfigSectionMeta>));

        var tryGet = registryType.GetMethod(nameof(IConfigSectionRegistry.TryGet));
        tryGet.Should().NotBeNull();
        tryGet!.ReturnType.Should().Be(typeof(bool));

        var tryGetParams = tryGet.GetParameters();
        tryGetParams.Should().HaveCount(2);
        tryGetParams[0].ParameterType.Should().Be(typeof(string));
        tryGetParams[1].ParameterType.Should().Be(typeof(ConfigSectionMeta).MakeByRefType());
    }
}
