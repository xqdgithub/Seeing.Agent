using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Tests;

internal static class AcpTestConfigRegistry
{
    public static ConfigSectionRegistry CreateAcpRegistry()
    {
        var registry = ConfigSectionRegistry.CreateWithSpine();
        registry.Register(new ConfigSectionMeta(
            AcpOptions.SectionName,
            "seeing.json",
            ConfigScope.UserOnly,
            typeof(AcpOptions)));
        return registry;
    }
}
