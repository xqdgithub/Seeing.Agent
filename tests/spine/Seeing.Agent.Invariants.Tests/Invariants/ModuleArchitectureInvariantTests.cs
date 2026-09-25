using FluentAssertions;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Invariants.Tests.Infrastructure;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Invariants;

/// <summary>
/// 模块生命周期架构守门：能力包不得有裸 <see cref="Microsoft.Extensions.Hosting.IHostedService"/>（收编进 Module
/// Activate/Deactivate）；<see cref="BuiltInCapabilitySets.Full"/> 必须覆盖全部已实现的 <c>ISeeingModule</c>
/// （<see cref="BuiltInCapabilitySets.FullExcludedModules"/> 中的可选在线外部源除外）。
/// </summary>
public class ModuleArchitectureInvariantTests
{
    [Fact]
    public void Capability_Assemblies_Should_Not_Declare_Bare_IHostedService()
    {
        var hits = SrcAssemblyScanner.FindBareHostedServiceTypes();

        hits.Should().BeEmpty(
            "能力包内不得有裸 IHostedService：注册型资源应随 Module Activate/Deactivate 收编，" +
            "长驻执行循环须实现 IModuleHostedService；命中: {0}",
            string.Join("; ", hits));
    }

    [Fact]
    public void Full_CapabilitySet_Should_Cover_All_Implemented_Modules()
    {
        var implemented = SrcAssemblyScanner.DiscoverImplementedModuleIds();

        // 扫描器自检：若程序集加载全部失败而返回空集，下面的差集断言会失去守门意义
        implemented.Should().Contain(
            ["filesystem", "memory", "scheduler", "acp", "systemone", "systemone.tools"],
            "扫描器应能从 src 构建产物反射发现内置模块");

        var missing = implemented
            .Except(BuiltInCapabilitySets.Full, StringComparer.Ordinal)
            .Except(BuiltInCapabilitySets.FullExcludedModules, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "全部已实现的 ISeeingModule 都必须登记进 BuiltInCapabilitySets.Full（或在 FullExcludedModules 显式豁免）；缺失: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void FullExcludedModules_Should_Be_Implemented_And_Absent_From_Full()
    {
        // 豁免清单自检：豁免项必须真实存在且确实不在 Full，避免陈旧/错误豁免削弱守门语义
        var implemented = SrcAssemblyScanner.DiscoverImplementedModuleIds();

        foreach (var excluded in BuiltInCapabilitySets.FullExcludedModules)
        {
            implemented.Should().Contain(excluded,
                "FullExcludedModules 中的 '{0}' 必须是真实存在的已实现模块", excluded);
            BuiltInCapabilitySets.Full.Should().NotContain(excluded,
                "FullExcludedModules 中的 '{0}' 不应出现在 Full 中", excluded);
        }
    }
}
