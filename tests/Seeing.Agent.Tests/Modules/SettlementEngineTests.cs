using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Modules;
using Xunit;

namespace Seeing.Agent.Tests.Modules;

public class SettlementEngineTests
{
    private static ModuleDescriptor Desc(
        string id,
        params string[] dependsOn)
        => new(id, Array.Empty<string>(), Array.Empty<string>(), dependsOn);

    private static SettlementEngine CreateEngine(out ModuleCatalog catalog)
    {
        catalog = new ModuleCatalog();
        return new SettlementEngine(catalog, NullLogger<SettlementEngine>.Instance);
    }

    [Fact]
    public async Task SettleAsync_目录外id不启用_且告警忽略()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("filesystem", "io.local")],
            ConfiguredScenario = "code",
            Scenarios = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = ["io.local", "filesystem", "not-referenced-pkg"],
            },
            UserEnabled = ["io.local", "filesystem", "ghost-module"],
        };

        var result = await engine.SettleAsync(input);

        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local"]);
        catalog.IsEnabled("ghost-module").Should().BeFalse();
        catalog.IsAvailable("ghost-module").Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("ghost-module", StringComparison.Ordinal));
        result.Warnings.Should().Contain(w => w.Contains("not-referenced-pkg", StringComparison.Ordinal));
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
                Desc("filesystem", "io.local"),
            ],
            // 仅启用 filesystem，缺少 io.local
            UserEnabled = ["filesystem"],
            HostDefaultScenario = null,
            ConfiguredScenario = null,
        };

        var act = async () => await engine.SettleAsync(input);

        await act.Should().ThrowAsync<SettlementException>()
            .WithMessage("*filesystem*io.local*");

        // available 已写入；enabled 因拒启未替换（仍为空）
        catalog.IsAvailable("filesystem").Should().BeTrue();
        catalog.IsEnabled("filesystem").Should().BeFalse();
        catalog.Enabled.Should().BeEmpty();
    }

    [Fact]
    public async Task SettleAsync_Scenario引用未在available的id_告警忽略不拒启()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("basic")],
            ConfiguredScenario = "minimal",
            Scenarios = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                // memory 未由宿主引用 → 不在 available
                ["minimal"] = ["io.local", "basic", "memory"],
            },
        };

        var result = await engine.SettleAsync(input);

        result.Enabled.Should().BeEquivalentTo(["basic", "io.local"]);
        catalog.IsEnabled("memory").Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.Contains("memory", StringComparison.Ordinal) &&
            w.Contains("available", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SettleAsync_用户enabled为null时使用scenario基线_并减去disabled()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available =
            [
                Desc("io.local"),
                Desc("filesystem", "io.local"),
                Desc("git", "io.local"),
                Desc("shell", "io.local"),
            ],
            HostDefaultScenario = "code",
            Scenarios = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = ["io.local", "filesystem", "git", "shell"],
            },
            UserEnabled = null,
            UserDisabled = ["git", "unknown-disabled"],
        };

        var result = await engine.SettleAsync(input);

        result.Scenario.Should().Be("code");
        result.Enabled.Should().BeEquivalentTo(["filesystem", "io.local", "shell"]);
        catalog.IsEnabled("git").Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("unknown-disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettleAsync_无scenario且无userEnabled_启用集为空()
    {
        var engine = CreateEngine(out var catalog);
        var input = new SettlementInput
        {
            Available = [Desc("io.local"), Desc("filesystem", "io.local")],
        };

        var result = await engine.SettleAsync(input);

        result.Scenario.Should().BeNull();
        result.Enabled.Should().BeEmpty();
        catalog.Enabled.Should().BeEmpty();
        catalog.Available.Should().HaveCount(2);
    }

    [Fact]
    public async Task SettleAsync_委托解析scenario优先于字典()
    {
        var engine = CreateEngine(out _);
        var input = new SettlementInput
        {
            Available = [Desc("a"), Desc("b")],
            ConfiguredScenario = "x",
            Scenarios = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = ["a"],
            },
            ResolveScenarioModules = name => name == "x" ? ["a", "b"] : null,
        };

        var result = await engine.SettleAsync(input);

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
        public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
