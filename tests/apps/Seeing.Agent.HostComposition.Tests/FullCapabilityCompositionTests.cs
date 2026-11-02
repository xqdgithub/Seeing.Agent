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

    /// <summary>复盘能力必须进 Full 档并随组合根一起登记（否则宿主开着却永远不触发）。</summary>
    [Fact]
    public void Full_CapabilitySet_Should_Include_Reflection()
    {
        BuiltInCapabilitySets.Full.Should().Contain("reflection");
    }

    /// <summary>组合根解析出的模块集合里必须有 reflection 模块实例。</summary>
    [Fact]
    public async Task Fully_Composed_Host_Should_Register_Reflection_Module()
    {
        await using var provider = BuildProvider(includeGatewayServer: false);

        provider.GetServices<ISeeingModule>()
            .Should().Contain(m => m.Id == "reflection");
    }

    /// <summary>
    /// 复盘能力默认不得自行启用：Enabled 默认 false，
    /// 用户不显式开启时不应产生任何后台模型调用（成本闸）。
    /// </summary>
    [Fact]
    public async Task Reflection_Should_Be_Disabled_By_Default()
    {
        await using var provider = BuildProvider(includeGatewayServer: false);

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptionsMonitor<Seeing.Agent.Reflection.Configuration.ReflectionOptions>>();

        options.CurrentValue.Enabled.Should().BeFalse(
            "复盘会产生额外的模型调用，必须由用户显式开启");
        options.CurrentValue.Mode.Should().Be(
            Seeing.Agent.Reflection.Configuration.ReflectionMode.SuggestOnly,
            "默认模式只产出建议、不写盘");
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
