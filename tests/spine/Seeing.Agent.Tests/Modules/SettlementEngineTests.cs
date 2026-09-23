using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Modules;

public class SettlementEngineTests
{
    private static ModuleDescriptor Desc(
        string id,
        params string[] dependsOn)
        => new(id, Array.Empty<string>(), Array.Empty<string>(), dependsOn);

    private static ModuleDescriptor DescSeam(
        string id,
        string[] providedSeams,
        params string[] dependsOn)
        => new(id, Array.Empty<string>(), providedSeams, dependsOn);

    private static SettlementEngine CreateEngine(out ModuleCatalog catalog)
    {
        catalog = new ModuleCatalog();
        return new SettlementEngine(catalog, NullLogger<SettlementEngine>.Instance);
    }

    [Fact]
    public async Task SettleAsync_BootStar_启用全部Available()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("filesystem", "io.local"), Desc("shell", "io.local")],
            ConfiguredBoot = "*",
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Boot.Should().Be("*");
        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local", "shell"]);
        catalog.Enabled.Should().BeEquivalentTo(result.Enabled);
    }

    [Fact]
    public async Task SettleAsync_BootNamed_Disabled扣减()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available =
            [
                Desc("io.local"),
                Desc("shell", "io.local"),
                Desc("mcp"),
                Desc("basic"),
            ],
            ConfiguredBoot = "secure",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["secure"] = new("secure", ["*"], ["shell", "mcp"]),
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Boot.Should().Be("secure");
        result.Enabled.Should().BeEquivalentTo(["basic", "io.local"]);
        result.Enabled.Should().NotContain("shell");
        result.Enabled.Should().NotContain("mcp");
    }

    [Fact]
    public async Task SettleAsync_BootMinimal_白名单求交Available()
    {
        var engine = CreateEngine(out _);
        var available = BuiltInCapabilitySets.Minimal
            .Select(id => Desc(id))
            .Concat([Desc("filesystem"), Desc("shell")])
            .ToArray();

        // 补依赖边：测试用无 DependsOn，仅验证白名单交
        var input = new SettlementInput
        {
            Available = available,
            ConfiguredBoot = "minimal",
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Boot.Should().Be("minimal");
        result.Enabled.Should().BeEquivalentTo(BuiltInCapabilitySets.Minimal);
        result.Enabled.Should().NotContain("filesystem");
        result.Enabled.Should().NotContain("shell");
    }

    [Fact]
    public async Task SettleAsync_StarModules_不进字面交_展开为Available()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("a"), Desc("b")],
            ConfiguredBoot = "star-set",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                // Modules 含 "*" —— 不得作为模块 id 进入 IntersectWithAvailable
                ["star-set"] = new("star-set", ["*"], Array.Empty<string>()),
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEquivalentTo(["a", "b"]);
        result.Warnings.Should().NotContain(w => w.Contains("'*'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettleAsync_EmptyModules_启用集为空()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("filesystem")],
            ConfiguredBoot = "empty",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["empty"] = new("empty", Array.Empty<string>(), Array.Empty<string>()),
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEmpty();
        catalog.Enabled.Should().BeEmpty();
        catalog.Available.Should().HaveCount(2);
    }

    [Fact]
    public async Task SettleAsync_DefaultScenario_不缩小Boot()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("filesystem"), Desc("shell")],
            ConfiguredBoot = "*",
            ConfiguredScenario = "minimal", // 诊断用默认工作模式，不得收缩 bootEnabled
            Scenarios = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["minimal"] = ["io.local"],
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Boot.Should().Be("*");
        result.Scenario.Should().Be("minimal");
        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local", "shell"]);
    }

    [Fact]
    public async Task SettleAsync_UnknownBoot_应拒启()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available = [Desc("io.local")],
            ConfiguredBoot = "does-not-exist",
        };

        var act = async () => await engine.SettleAsync(input);

        await act.Should().ThrowAsync<SettlementException>()
            .WithMessage("*does-not-exist*");
        catalog.Enabled.Should().BeEmpty();
        catalog.IsAvailable("io.local").Should().BeTrue();
    }

    [Fact]
    public async Task SettleAsync_ModulesEnabled_忽略并警告()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("filesystem"), Desc("shell")],
            ConfiguredBoot = "*",
            UserEnabled = ["io.local"], // 已废除：不得收窄 boot
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local", "shell"]);
        result.Warnings.Should().Contain(w =>
            w.Contains("Modules.Enabled", StringComparison.Ordinal) &&
            w.Contains("CapabilitySets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettleAsync_硬依赖缺失_应拒启()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available =
            [
                Desc("io.local"),
                Desc("shell", "io.local"),
            ],
            ConfiguredBoot = "broken",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["broken"] = new("broken", ["shell"], Array.Empty<string>()),
            },
        };

        var act = async () => await engine.SettleAsync(input);

        await act.Should().ThrowAsync<SettlementException>()
            .WithMessage("*shell*io.local*");
        catalog.IsAvailable("shell").Should().BeTrue();
        catalog.Enabled.Should().BeEmpty();
    }

    [Fact]
    public async Task SettleAsync_UserDisabled_从Boot扣减()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available =
            [
                Desc("io.local"),
                Desc("filesystem", "io.local"),
                Desc("git", "io.local"),
                Desc("shell", "io.local"),
            ],
            ConfiguredBoot = "code",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = new("code", ["io.local", "filesystem", "git", "shell"], Array.Empty<string>()),
            },
            UserDisabled = ["git", "unknown-disabled"],
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local", "shell"]);
        result.Warnings.Should().Contain(w => w.Contains("unknown-disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettleAsync_默认Boot为星号_无配置时启用全部Available()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("filesystem")],
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Boot.Should().Be("*");
        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local"]);
    }

    [Fact]
    public async Task SettleAsync_BootOverride优先于文件Boot()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("a"), Desc("b")],
            ConfiguredBoot = "*",
            BootOverride = "narrow",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["narrow"] = new("narrow", ["a"], Array.Empty<string>()),
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Boot.Should().Be("narrow");
        result.Enabled.Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public async Task SettleAsync_HostDefaultBoot_文件缺省时使用()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("a"), Desc("b")],
            HostDefaultBoot = "narrow",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["narrow"] = new("narrow", ["a"], Array.Empty<string>()),
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Boot.Should().Be("narrow");
        result.Enabled.Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public async Task SettleAsync_BoundSeams_来自Seams与HostDefault()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available =
            [
                DescSeam("io.local", ["executionWorld"]),
                Desc("filesystem", "io.local"),
            ],
            ConfiguredBoot = "*",
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.BoundSeams.Should().ContainKey("executionWorld")
            .WhoseValue.Should().Be("io.local");
        catalog.BoundSeams["executionWorld"].Should().Be("io.local");
    }

    [Fact]
    public async Task SettleAsync_UserSeams覆盖HostDefault()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available =
            [
                DescSeam("io.local", ["executionWorld"]),
                DescSeam("io.sandbox", ["executionWorld"]),
                Desc("filesystem", "io.sandbox"),
            ],
            ConfiguredBoot = "*",
            // 仅启用 sandbox 提供方（避免多提供方冲突）
            UserDisabled = ["io.local"],
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
            UserSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.sandbox",
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.BoundSeams["executionWorld"].Should().Be("io.sandbox");
    }

    [Fact]
    public async Task SettleAsync_ScenarioSeams不驱动进程绑定()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available =
            [
                DescSeam("io.local", ["executionWorld"]),
                Desc("filesystem", "io.local"),
            ],
            ConfiguredBoot = "*",
            ConfiguredScenario = "code",
            ResolveScenarioSeams = _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
            // 无 UserSeams / HostDefaultSeams → 不得从 Scenario 绑定
        };

        var act = async () => await engine.SettleAsync(input);

        await act.Should().ThrowAsync<SettlementException>()
            .WithMessage("*executionWorld*未绑定*消费方*");
    }

    [Fact]
    public async Task SettleAsync_Seam提供方不在bootEnabled_应拒启()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available =
            [
                DescSeam("io.local", ["executionWorld"]),
                Desc("filesystem", "io.local"),
            ],
            ConfiguredBoot = "no-io",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["no-io"] = new("no-io", ["filesystem"], Array.Empty<string>()),
            },
            UserSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
        };

        // filesystem DependsOn io.local → 先硬依赖拒启；改为无 DependsOn 测 seam
        input = new SettlementInput
        {
            Available =
            [
                DescSeam("io.local", ["executionWorld"]),
                Desc("basic"),
            ],
            ConfiguredBoot = "no-io",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["no-io"] = new("no-io", ["basic"], Array.Empty<string>()),
            },
            UserSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
        };

        var act = async () => await engine.SettleAsync(input);

        await act.Should().ThrowAsync<SettlementException>()
            .WithMessage("*executionWorld*io.local*bootEnabled*");
    }

    [Fact]
    public async Task SettleAsync_同一seam多提供方启用_应拒启()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available =
            [
                DescSeam("io.local", ["executionWorld"]),
                DescSeam("io.sandbox", ["executionWorld"]),
            ],
            ConfiguredBoot = "*",
        };

        var act = async () => await engine.SettleAsync(input);

        await act.Should().ThrowAsync<SettlementException>()
            .WithMessage("*executionWorld*多个*");
        catalog.Enabled.Should().BeEmpty();
    }

    [Fact]
    public async Task SettleAsync_seams按模块id绑定executionWorld_应成功()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available =
            [
                DescSeam("io.local", ["executionWorld"]),
                DescSeam("io.sandbox", ["executionWorld"]),
                Desc("filesystem", "io.local"),
            ],
            ConfiguredBoot = "with-io",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["with-io"] = new("with-io", ["io.local", "filesystem"], Array.Empty<string>()),
            },
            UserSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local"]);
        result.BoundSeams.Should().ContainKey("executionWorld")
            .WhoseValue.Should().Be("io.local");
        catalog.BoundSeams["executionWorld"].Should().Be("io.local");
    }

    [Fact]
    public async Task SettleAsync_委托解析CapabilitySet优先于字典()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("a"), Desc("b")],
            ConfiguredBoot = "x",
            CapabilitySets = new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = new("x", ["a"], Array.Empty<string>()),
            },
            ResolveCapabilitySet = name => name == "x"
                ? new CapabilitySetDefinition("x", ["a", "b"], Array.Empty<string>())
                : null,
        };

        var result = await engine.SettleAsync(input, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void ModuleCatalog_IsEnabled_IsAvailable_与Unhealthy()
    {
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable([Desc("io.local"), Desc("filesystem", "io.local")]);
        catalog.ReplaceEnabled(["io.local"]);
        catalog.MarkUnhealthy("filesystem", "activate failed");

        catalog.IsAvailable("io.local").Should().BeTrue();
        catalog.IsEnabled("io.local").Should().BeTrue();
        catalog.IsEnabled("filesystem").Should().BeFalse();
        catalog.Unhealthy.Should().ContainKey("filesystem")
            .WhoseValue.Should().Be("activate failed");

        catalog.ClearUnhealthy("filesystem");
        catalog.Unhealthy.Should().BeEmpty();
    }

    [Fact]
    public void ToDescriptors_从ISeeingModule投影()
    {
        var module = new FakeModule("filesystem", dependsOn: ["io.local"]);
        var descriptors = SettlementEngine.ToDescriptors([module]);

        descriptors.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new ModuleDescriptor("filesystem", ["read"], ["seam"], ["io.local"]));
    }

    private sealed class FakeModule : ISeeingModule
    {
        public FakeModule(string id, string[] dependsOn)
        {
            Id = id;
            DependsOn = dependsOn;
        }

        public string Id { get; }
        public IReadOnlyList<string> ProvidedTools { get; } = ["read"];
        public IReadOnlyList<string> ProvidedSeams { get; } = ["seam"];
        public IReadOnlyList<string> DependsOn { get; }
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
