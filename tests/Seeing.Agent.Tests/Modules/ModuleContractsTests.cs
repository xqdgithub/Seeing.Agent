using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Modules;

public class ModuleContractsTests
{
    [Fact]
    public void ISeeingModule_声明元数据与生命周期成员()
    {
        var type = typeof(ISeeingModule);

        type.GetProperty(nameof(ISeeingModule.Id))!.PropertyType.Should().Be(typeof(string));
        type.GetProperty(nameof(ISeeingModule.ProvidedTools))!.PropertyType.Should().Be(typeof(IReadOnlyList<string>));
        type.GetProperty(nameof(ISeeingModule.ProvidedSeams))!.PropertyType.Should().Be(typeof(IReadOnlyList<string>));
        type.GetProperty(nameof(ISeeingModule.DependsOn))!.PropertyType.Should().Be(typeof(IReadOnlyList<string>));

        var configure = type.GetMethod(nameof(ISeeingModule.ConfigureServices));
        configure.Should().NotBeNull();
        configure!.GetParameters().Single().ParameterType.Should().Be(typeof(IServiceCollection));

        var activate = type.GetMethod(nameof(ISeeingModule.ActivateAsync));
        activate.Should().NotBeNull();
        activate!.ReturnType.Should().Be(typeof(Task));

        var deactivate = type.GetMethod(nameof(ISeeingModule.DeactivateAsync));
        deactivate.Should().NotBeNull();
        deactivate!.ReturnType.Should().Be(typeof(Task));
    }

    [Fact]
    public void IModuleCatalog_Available_类型为ModuleDescriptor集合()
    {
        var property = typeof(IModuleCatalog).GetProperty(nameof(IModuleCatalog.Available));
        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(IReadOnlyCollection<ModuleDescriptor>));
    }

    [Fact]
    public void IModuleCatalog_声明Enabled_Unhealthy与查询方法()
    {
        var type = typeof(IModuleCatalog);

        type.GetProperty(nameof(IModuleCatalog.Enabled))!.PropertyType
            .Should().Be(typeof(IReadOnlyCollection<string>));
        type.GetProperty(nameof(IModuleCatalog.Unhealthy))!.PropertyType
            .Should().Be(typeof(IReadOnlyDictionary<string, string?>));

        type.GetMethod(nameof(IModuleCatalog.IsEnabled))!.ReturnType.Should().Be(typeof(bool));
        type.GetMethod(nameof(IModuleCatalog.IsAvailable))!.ReturnType.Should().Be(typeof(bool));
    }

    [Fact]
    public void ModuleDescriptor_为sealed_record且字段可构造()
    {
        typeof(ModuleDescriptor).IsSealed.Should().BeTrue();

        var descriptor = new ModuleDescriptor(
            "filesystem",
            ["read_file", "write_file"],
            ["executionWorld"],
            ["io.local"]);

        descriptor.Id.Should().Be("filesystem");
        descriptor.ProvidedTools.Should().Equal("read_file", "write_file");
        descriptor.ProvidedSeams.Should().Equal("executionWorld");
        descriptor.DependsOn.Should().Equal("io.local");
    }
}
