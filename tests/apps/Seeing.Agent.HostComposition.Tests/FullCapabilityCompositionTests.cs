using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Hosting;

namespace Seeing.Agent.HostComposition.Tests;

/// <summary>
/// 组合根一致性守门：任何宿主都必须通过 <see cref="FullCapabilityComposition.AddSeeingFullCapabilities"/>
/// 得到与 <see cref="BuiltInCapabilitySets.Full"/> 完全一致的默认模块集。
/// </summary>
public class FullCapabilityCompositionTests
{
    /// <summary>共享组合根 + 宿主会话托管（subagent）应恰好等于 Full。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Composition_PlusSessionHosting_ShouldRegisterExactlyFullModuleIds(bool includeGatewayServer)
    {
        await using var provider = BuildProvider(includeGatewayServer);

        var registered = provider.GetServices<ISeeingModule>()
            .Select(m => m.Id)
            .ToArray();

        registered.Should().BeEquivalentTo(BuiltInCapabilitySets.Full);
    }

    /// <summary>Boot=full 时结算结果应恰好启用 Full（不允许宿主漏注册）。</summary>
    [Fact]
    public async Task Settlement_BootFull_ShouldEnableExactlyFull()
    {
        await using var provider = BuildProvider(includeGatewayServer: true);

        var descriptors = SettlementEngine.ToDescriptors(provider.GetServices<ISeeingModule>());
        var engine = new SettlementEngine(new ModuleCatalog());

        var result = await engine.SettleAsync(new SettlementInput
        {
            Available = descriptors,
            ConfiguredBoot = "full",
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
        }, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEquivalentTo(BuiltInCapabilitySets.Full);
    }

    /// <summary>4 个宿主组合根必须走共享注册点，禁止再出现分散的 AddSeeingModule。</summary>
    [Theory]
    [InlineData("samples/Seeing.Agent.WebUI/Program.cs")]
    [InlineData("samples/Seeing.Agent.Tui/Program.cs")]
    [InlineData("samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs")]
    [InlineData("samples/Seeing.Gateway.Server/Program.cs")]
    public void HostSamples_ShouldUseSharedComposition(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(path).Should().BeTrue($"{relativePath} 应存在");

        var source = File.ReadAllText(path);

        source.Should().Contain(
            "AddSeeingFullCapabilities",
            "{0} 必须使用共享组合根，避免默认能力集再次漂移",
            relativePath);
        source.Should().NotContain(
            "AddSeeingModule<",
            "{0} 不应再手写能力模块注册；新增模块请改 samples/Seeing.Agent.HostComposition",
            relativePath);
    }

    private static ServiceProvider BuildProvider(bool includeGatewayServer)
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<IConfiguration>(configuration);

        services.AddSeeingFullCapabilities(registry, configuration, includeGatewayServer);

        // 宿主通过各自 Host Shape 提供 subagent（HostingModule）；Gateway.Server 显式补调。
        services.AddSessionHosting();
        services.AddExecutionEngine();

        services.AddSeeingCore(registry);

        return services.BuildServiceProvider();
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("测试必须能定位仓库根（Seeing.Agent.slnx）");
        return dir!.FullName;
    }
}
