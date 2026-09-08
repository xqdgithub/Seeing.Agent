using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Mcp;
using Seeing.Agent.Skills;
using Seeing.Agent.WebUI.Pages;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class ModuleRouteResolverTests
{
    [Fact]
    public async Task DisabledModuleDeepLink_ShouldResolveNotEnabled()
    {
        var registry = new UiContributionRegistry();
        // skills 未 Activate → Routes 无 /skills

        var result = ModuleRouteResolver.Resolve("/skills", registry);

        result.Kind.Should().Be(ModuleRouteKind.NotEnabled);
        result.ModuleId.Should().Be("skills");
        result.FeatureTitle.Should().Be("技能");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EnabledModule_ShouldResolveFoundViaRegistry()
    {
        var registry = new UiContributionRegistry();
        var module = new SkillsModule(registry);
        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider());
        ModulePageRouteBinder.BindExistingPages(registry);

        var result = ModuleRouteResolver.Resolve("/skills", registry);

        result.Kind.Should().Be(ModuleRouteKind.Found);
        result.ComponentType.Should().Be(typeof(SkillsPage));
        result.FeatureTitle.Should().Be("技能");
    }

    [Fact]
    public async Task EnabledDetailRoute_ShouldExtractParameters()
    {
        var registry = new UiContributionRegistry();
        var module = new SkillsModule(registry);
        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider());
        ModulePageRouteBinder.BindExistingPages(registry);

        var result = ModuleRouteResolver.Resolve("/skills/my-skill", registry);

        result.Kind.Should().Be(ModuleRouteKind.Found);
        result.ComponentType.Should().Be(typeof(SkillDetailPage));
        result.Parameters.Should().ContainKey("SkillName");
        result.Parameters!["SkillName"].Should().Be("my-skill");
    }

    [Fact]
    public async Task EnabledButCatalogDisabled_ShouldResolveNotEnabled()
    {
        var registry = new UiContributionRegistry();
        var module = new McpModule(registry);
        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider());
        ModulePageRouteBinder.BindExistingPages(registry);

        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(c => c.IsEnabled("mcp")).Returns(false);

        var result = ModuleRouteResolver.Resolve("/mcp", registry, catalog.Object);

        result.Kind.Should().Be(ModuleRouteKind.NotEnabled);
        result.ModuleId.Should().Be("mcp");
    }

    [Fact]
    public void UnknownNonModulePath_ShouldFallback()
    {
        var registry = new UiContributionRegistry();

        var result = ModuleRouteResolver.Resolve("/totally-unknown-path", registry);

        result.Kind.Should().Be(ModuleRouteKind.Fallback);
    }

    [Fact]
    public void CoreShellRoute_ShouldResolveFound()
    {
        var registry = new UiContributionRegistry();
        registry.Register(new CoreShellUiContribution());

        var result = ModuleRouteResolver.Resolve("/sessions", registry);

        result.Kind.Should().Be(ModuleRouteKind.Found);
        result.ComponentType.Should().Be(typeof(SessionsPage));
    }

    [Fact]
    public void CoreShellSessionRoute_ShouldResolveFoundWithParameter()
    {
        var registry = new UiContributionRegistry();
        registry.Register(new CoreShellUiContribution());

        var root = ModuleRouteResolver.Resolve("/", registry);
        root.Kind.Should().Be(ModuleRouteKind.Found);
        root.ComponentType.Should().Be(typeof(global::Seeing.Agent.WebUI.Pages.Index));

        var session = ModuleRouteResolver.Resolve("/session/abc-123", registry);
        session.Kind.Should().Be(ModuleRouteKind.Found);
        session.ComponentType.Should().Be(typeof(global::Seeing.Agent.WebUI.Pages.Session));
        session.Parameters.Should().ContainKey("SessionId");
        session.Parameters!["SessionId"].Should().Be("abc-123");

        var settings = ModuleRouteResolver.Resolve("/settings", registry);
        settings.Kind.Should().Be(ModuleRouteKind.Found);
        settings.ComponentType.Should().Be(typeof(Settings));
    }

    [Fact]
    public void CatchAllMemoryDetail_ShouldCapturePath()
    {
        ModuleRouteResolver.TryMatchTemplate(
                "/memory/detail/{*FilePath}",
                "/memory/detail/notes/a.md",
                out var parameters,
                out _)
            .Should().BeTrue();

        parameters["FilePath"].Should().Be("notes/a.md");
    }

    [Fact]
    public void SkillsCreate_ShouldBeatSkillNameTemplate()
    {
        var registry = new UiContributionRegistry();
        registry.Register(new BoundNav(
            "skills",
            [
                new NavContribution("/skills", "技能", "star", ["skills"], ComponentType: typeof(SkillsPage)),
                new NavContribution("/skills/create", "创建", "star", ["skills"], ComponentType: typeof(SkillCreatePage)),
                new NavContribution("/skills/{SkillName}", "详情", "star", ["skills"], ComponentType: typeof(SkillDetailPage)),
            ]));

        var create = ModuleRouteResolver.Resolve("/skills/create", registry);
        create.ComponentType.Should().Be(typeof(SkillCreatePage));

        var detail = ModuleRouteResolver.Resolve("/skills/create-me", registry);
        detail.ComponentType.Should().Be(typeof(SkillDetailPage));
        detail.Parameters!["SkillName"].Should().Be("create-me");
    }

    private sealed class BoundNav(string moduleId, IReadOnlyList<object> items) : IUiContribution
    {
        public string ModuleId { get; } = moduleId;
        public IReadOnlyList<object> Contribute() => items;
    }
}
