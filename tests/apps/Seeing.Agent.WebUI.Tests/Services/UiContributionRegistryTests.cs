using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Mcp;
using Seeing.Agent.Skills;
using Seeing.Agent.Core.Tools.Basic;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.Pages;

namespace Seeing.Agent.WebUI.Tests.Services;

public class UiContributionRegistryTests
{
    [Fact]
    public async Task Activate_RegistersNav_Deactivate_Removes()
    {
        var registry = new UiContributionRegistry();
        var module = new SkillsModule(registry);

        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);

        registry.NavItems.Should().ContainSingle(n => n.Route == "/skills" && n.Title == "技能");
        registry.Routes.Should().ContainKey("/skills");

        await module.DeactivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);

        registry.NavItems.Should().BeEmpty();
        registry.Routes.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleModules_ActivateDeactivate_IsSymmetric()
    {
        var registry = new UiContributionRegistry();
        var skills = new SkillsModule(registry);
        var mcp = new McpModule(registry);
        var basic = new BasicModule(registry);

        await skills.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);
        await mcp.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);
        await basic.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);

        registry.NavItems.Select(n => n.Route).Should().BeEquivalentTo("/skills", "/mcp", "/tools");

        await mcp.DeactivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);
        registry.NavItems.Select(n => n.Route).Should().BeEquivalentTo("/skills", "/tools");
        registry.Routes.Should().NotContainKey("/mcp");

        await skills.DeactivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);
        await basic.DeactivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);
        registry.NavItems.Should().BeEmpty();
    }

    [Fact]
    public async Task BindExistingPages_AttachesComponentTypes()
    {
        var registry = new UiContributionRegistry();
        var module = new SkillsModule(registry);
        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);

        ModulePageRouteBinder.BindExistingPages(registry);

        registry.Routes["/skills"].ComponentType.Should().Be(typeof(SkillsPage));
        registry.Routes.Should().ContainKey("/skills/{SkillName}");
        registry.Routes["/skills/{SkillName}"].ComponentType.Should().Be(typeof(SkillDetailPage));
    }

    [Fact]
    public void Register_MergesContributionKinds()
    {
        var registry = new UiContributionRegistry();
        registry.Register(new FakeContribution(
            "demo",
            [
                new NavContribution("/demo", "Demo", "appstore", ["demo"]),
                new RouteContribution("/demo/hidden", "Hidden", typeof(object)),
                new SettingsCardContribution("/settings/demo", "Demo", typeof(object), ["demo"]),
                new SlotContribution("session_prompt", typeof(object), ["demo"]),
                new MessageRendererContribution("demo-msg", typeof(object)),
            ]));

        registry.NavItems.Should().ContainSingle(n => n.Route == "/demo");
        registry.NavItems.Should().NotContain(n => n.Route == "/demo/hidden");
        registry.SettingsCards.Should().ContainSingle();
        registry.Slots.Should().ContainSingle();
        registry.MessageRenderers.Should().ContainSingle();
        registry.Routes["/demo"].Title.Should().Be("Demo");
        registry.Routes["/demo/hidden"].ComponentType.Should().Be(typeof(object));
        registry.Routes["/demo/hidden"].Title.Should().Be("Hidden");
    }

    private sealed class FakeContribution(string moduleId, IReadOnlyList<object> items) : IUiContribution
    {
        public string ModuleId { get; } = moduleId;
        public IReadOnlyList<object> Contribute() => items;
    }
}
