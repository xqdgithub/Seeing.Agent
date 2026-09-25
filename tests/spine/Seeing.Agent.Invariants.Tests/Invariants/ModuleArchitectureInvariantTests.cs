using FluentAssertions;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Invariants.Tests.Infrastructure;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Invariants;

/// <summary>
/// 模块生命周期架构守门：能力包不得有裸 <see cref="Microsoft.Extensions.Hosting.IHostedService"/>（收编进 Module
/// Activate/Deactivate）；<see cref="BuiltInCapabilitySets.Full"/> 必须覆盖全部已实现的 <c>ISeeingModule</c>。
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
            .ToList();

        missing.Should().BeEmpty(
            "全部已实现的 ISeeingModule 都必须登记进 BuiltInCapabilitySets.Full；缺失: {0}",
            string.Join(", ", missing));
    }
}
