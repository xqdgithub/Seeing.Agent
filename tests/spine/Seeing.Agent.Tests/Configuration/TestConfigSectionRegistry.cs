using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Skills.Configuration;
using Seeing.Agent.TokenBudget.Configuration;

namespace Seeing.Agent.Tests.Configuration;

internal static class TestConfigSectionRegistry
{
    public static ConfigSectionRegistry CreateSpine() => ConfigSectionRegistry.CreateWithSpine();

    public static ConfigSectionRegistry CreateSpineWith(params ConfigSectionMeta[] extras)
    {
        var registry = CreateSpine();
        foreach (var meta in extras)
            registry.Register(meta);
        return registry;
    }

    public static ConfigSectionRegistry WithAcp() => CreateSpineWith(
        new ConfigSectionMeta(AcpOptions.SectionName, "seeing.json", ConfigScope.UserOnly, typeof(AcpOptions)));

    public static ConfigSectionRegistry WithGateway() => CreateSpineWith(
        new ConfigSectionMeta(GatewayOptions.SectionName, "seeing.json", ConfigScope.ProjectOnly, typeof(GatewayOptions)),
        new ConfigSectionMeta(GatewayClientsOptions.SectionName, "seeing.json", ConfigScope.ProjectOnly, typeof(GatewayClientsOptions)));

    public static ConfigSectionRegistry WithSkills() => CreateSpineWith(
        new ConfigSectionMeta(SkillsOptions.SectionName, "seeing.json", ConfigScope.Both, typeof(SkillsOptions)));

    public static ConfigSectionRegistry WithTokenBudget() => CreateSpineWith(
        new ConfigSectionMeta(TokenBudgetOptions.SectionName, "seeing.json", ConfigScope.Both, typeof(TokenBudgetOptions)));

    public static ConfigSectionRegistry WithShell() => CreateSpineWith(
        new ConfigSectionMeta("Shell", "seeing.json", ConfigScope.Both, typeof(ShellOptions)));
}
