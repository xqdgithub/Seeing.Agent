using FluentAssertions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.UiEnvelopeProbe;

namespace Seeing.Agent.UiEnvelopeProbe.Tests;

/// <summary>
/// 收集链：宿主段 / 模块段 / 三条入口 / 异常隔离。
/// </summary>
public class CollectorTests
{
    private static readonly UiPoint PagePoint = new()
    { Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page };

    private static UiPointCatalog Points() => new([PagePoint]);

    private static PageContributionProbe Page(string id, string route, params string[] requires) => new()
    { Point = PagePoint.Id, Id = id, Route = route, Title = id, ViewKey = id, Requires = requires };

    private sealed class FakeHost : IUiHostContributor
    {
        private readonly Func<IReadOnlyList<UiContribution>> _describe;
        public FakeHost(string ownerId, Func<IReadOnlyList<UiContribution>> describe)
        {
            OwnerId = ownerId;
            _describe = describe;
        }

        public string OwnerId { get; }
        public IReadOnlyList<UiContribution> DescribeUi() => _describe();
    }

    private sealed class FakeModule : ISeeingModule, IUiContributor
    {
        private readonly Func<IReadOnlyList<UiContribution>> _describe;

        public FakeModule(string id, Func<IReadOnlyList<UiContribution>>? describe = null)
        {
            Id = id;
            _describe = describe ?? (() => []);
        }

        public string Id { get; }
        public IReadOnlyList<string> ProvidedTools => [];
        public IReadOnlyList<string> ProvidedSeams => [];
        public IReadOnlyList<string> DependsOn => [];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider s, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(IServiceProvider s, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<UiContribution> DescribeUi() => _describe();
    }

    private sealed class PlainModule : ISeeingModule
    {
        public PlainModule(string id) => Id = id;
        public string Id { get; }
        public IReadOnlyList<string> ProvidedTools => [];
        public IReadOnlyList<string> ProvidedSeams => [];
        public IReadOnlyList<string> DependsOn => [];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider s, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(IServiceProvider s, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── 宿主段 ──

    [Fact]
    public void HostContributions_ShouldBeCollected()
    {
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        var count = collector.CollectHostContributions([
            new FakeHost("host.webui", () => [Page("p.home", "/")])
        ]);

        count.Should().Be(1);
        registry.All.Should().ContainSingle();
        registry.All[0].OwnerId.Should().Be("host.webui");
    }

    [Fact]
    public void HostWithInvalidPrefix_ShouldBeRejectedWithDiagnostic()
    {
        // 关键防线：壳层 owner 若叫 __webui.shell__，其贡献会因「模块贡献需要 Requires」被整批拒收
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        var count = collector.CollectHostContributions([
            new FakeHost("__webui.shell__", () => [Page("p.home", "/")])
        ]);

        count.Should().Be(0);
        registry.All.Should().BeEmpty();
    }

    [Fact]
    public void HostEmptyRequires_ShouldBeAccepted()
    {
        // 宿主贡献天然是进程级，空 Requires 合法
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        collector.CollectHostContributions([new FakeHost("host.webui", () => [Page("p.home", "/")])]);

        registry.Diagnostics.Should().BeEmpty();
    }

    // ── 模块段 ──

    [Fact]
    public void ModuleContributions_ShouldRequireOwnership()
    {
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        collector.CollectModuleContributions([
            new FakeModule("skills", () => [Page("p.skills", "/skills", "skills")])
        ]);

        registry.All.Should().ContainSingle();
        registry.All[0].OwnerId.Should().Be("skills");
    }

    [Fact]
    public void ModuleWithEmptyRequires_ShouldBeRejected()
    {
        // 模块贡献空 Requires → 会变成进程级 → 模块停用后页面仍存活但工具已卸载
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        collector.CollectModuleContributions([
            new FakeModule("skills", () => [Page("p.skills", "/skills")])
        ]);

        registry.All.Should().BeEmpty();
        registry.Diagnostics.Should().ContainSingle()
            .Which.Kind.Should().Be(UiDiagnosticKind.InvalidDeclaration);
    }

    [Fact]
    public void NonContributorModules_ShouldBeSkipped()
    {
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        var count = collector.CollectModuleContributions([new PlainModule("llm.openai")]);

        count.Should().Be(0);
        registry.All.Should().BeEmpty();
    }

    [Fact]
    public void AllModules_ShouldBeScanned_IncludingDisabled()
    {
        // 「声明早于激活」：未启用模块的声明也必须在表里，否则直链拿不到「未启用」提示
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        collector.CollectModuleContributions([
            new FakeModule("skills", () => [Page("p.skills", "/skills", "skills")]),
            new FakeModule("memory", () => [Page("p.memory", "/memory", "memory")])
        ]);

        registry.All.Should().HaveCount(2);
    }

    // ── 异常隔离 ──

    [Fact]
    public void ThrowingModule_ShouldNotBreakOthers()
    {
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        collector.CollectModuleContributions([
            new FakeModule("boom", () => throw new InvalidOperationException("declare failed")),
            new FakeModule("skills", () => [Page("p.skills", "/skills", "skills")])
        ]);

        registry.All.Should().ContainSingle("一个模块声明失败不应影响其他模块");
        registry.Diagnostics.Should().Contain(d => d.Kind == UiDiagnosticKind.InvalidDeclaration);
    }

    [Fact]
    public void NullDeclaringModule_ShouldBeRecordedNotThrow()
    {
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());

        var act = () => collector.CollectModuleContributions([
            new FakeModule("nullmod", () => null!)
        ]);

        act.Should().NotThrow();
        registry.Diagnostics.Should().ContainSingle()
            .Which.Kind.Should().Be(UiDiagnosticKind.InvalidDeclaration);
    }

    // ── no-op ──

    [Fact]
    public void NoPointCatalog_ShouldCollectNothing()
    {
        var registry = new UiContributionRegistry(points: null);
        var collector = new UiContributionCollector(registry, points: null);

        collector.IsEnabled.Should().BeFalse();
        collector.CollectHostContributions([new FakeHost("host.webui", () => [Page("p", "/")])])
            .Should().Be(0);
        collector.CollectModuleContributions([new FakeModule("m", () => [Page("p", "/x", "m")])])
            .Should().Be(0);

        registry.All.Should().BeEmpty();
    }

    // ── 幂等（Deactivate→Activate 循环）──

    [Fact]
    public void RepeatedCollection_ShouldBeIdempotent()
    {
        var registry = new UiContributionRegistry(Points());
        var collector = new UiContributionCollector(registry, Points());
        var modules = new[] { new FakeModule("skills", () => [Page("p.skills", "/skills", "skills")]) };

        collector.CollectModuleContributions(modules);
        collector.CollectModuleContributions(modules);

        registry.All.Should().ContainSingle();
        registry.Diagnostics.Should().BeEmpty();
    }
}
