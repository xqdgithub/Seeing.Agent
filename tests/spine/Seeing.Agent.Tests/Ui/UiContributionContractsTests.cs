using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Xunit;

namespace Seeing.Agent.Tests.Ui;

/// <summary>
/// UI 信封契约的成员形状守卫。
/// <para>
/// 旧形状（<c>IUiContribution</c> / <c>NavContribution</c> / <c>RouteContribution</c> /
/// <c>LegacySettingsCardContribution</c> / <c>LegacySlotContribution</c>）已随批次 D 删除，
/// 这里钉住替代它们的「扩展点 + 载荷 + 注册表」三件套的成员集合，
/// 避免有人为了兼容又把带 <c>ComponentType</c> 或 <c>ModuleId</c> 的字段加回契约层。
/// </para>
/// </summary>
public class UiContributionContractsTests
{
    [Fact]
    public void UiPoint_ShouldCarryScopeAndPayloadKind()
    {
        var point = new UiPoint
        {
            Id = "session.prompt",
            DisplayName = "输入区上方",
            PayloadKind = UiPayloadKind.Slot,
            Scope = UiVisibilityScope.Session,
        };

        point.PayloadKind.Should().Be(UiPayloadKind.Slot);
        point.Scope.Should().Be(UiVisibilityScope.Session);
        point.MaxItems.Should().Be(int.MaxValue, "未声明上限时视为不限");
    }

    [Fact]
    public void UiPoint_ScopeDefaultsToProcess()
    {
        var point = new UiPoint
        {
            Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page,
        };

        point.Scope.Should().Be(UiVisibilityScope.Process,
            "侧栏/设置卡/页面不随会话工作模式收窄，默认必须是进程级");
    }

    [Fact]
    public void SettingsCardContribution_ShouldCarryTitleAndViewKey()
    {
        var card = new SettingsCardContribution
        {
            Point = "shell.settings.card",
            Id = "settings.memory",
            Title = "记忆",
            ViewKey = "settings.memory",
            Requires = ["memory"],
        };

        card.Title.Should().Be("记忆");
        card.ViewKey.Should().Be("settings.memory");
        card.Requires.Should().Equal("memory");
        card.Order.Should().Be(0);
    }

    [Fact]
    public void SlotContribution_ShouldCarryViewKeyAndOptionalDisplayName()
    {
        var slot = new SlotContribution
        {
            Point = "session.file.panel",
            Id = "slot.file.panel",
            ViewKey = "slot.file.panel",
            Requires = ["files.workspace"],
        };

        slot.ViewKey.Should().Be("slot.file.panel");
        slot.DisplayName.Should().BeNull();
        slot.Requires.Should().Equal("files.workspace");
    }

    [Fact]
    public void IUiContributionRegistry_ShouldExposeAddRangeRemoveAndQueries()
    {
        var registry = typeof(IUiContributionRegistry);

        var addRange = registry.GetMethod(nameof(IUiContributionRegistry.AddRange));
        addRange.Should().NotBeNull();
        addRange!.GetParameters().Select(p => p.ParameterType).Should().Equal(
            typeof(string), typeof(IReadOnlyList<UiContribution>));

        registry.GetMethod(nameof(IUiContributionRegistry.RemoveByOwner))!
            .GetParameters().Should().ContainSingle()
            .Which.ParameterType.Should().Be(typeof(string));

        registry.GetMethod(nameof(IUiContributionRegistry.ReportInvalidDeclaration))!
            .GetParameters().Select(p => p.ParameterType).Should().Equal(
                typeof(string), typeof(string));

        registry.GetProperty(nameof(IUiContributionRegistry.All))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<UiContributionEntry>));

        registry.GetProperty(nameof(IUiContributionRegistry.Pages))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<PageContribution>));

        registry.GetProperty(nameof(IUiContributionRegistry.Diagnostics))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<UiContributionDiagnostic>));

        registry.GetEvent(nameof(IUiContributionRegistry.Changed)).Should().NotBeNull();
    }

    [Fact]
    public void IUiContributionRegistry_ShouldNotResurrectLegacyMembers()
    {
        var registry = typeof(IUiContributionRegistry);

        foreach (var legacy in new[] { "Register", "Unregister", "NavItems", "SettingsCards", "Slots", "Routes" })
            registry.GetMember(legacy).Should().BeEmpty(
                $"'{legacy}' 属于已删除的旧通道；新契约只有 AddRange/RemoveByOwner/All/Get<T>/Pages/Diagnostics");
    }

    [Fact]
    public void NavGroups_ShouldKeepThreeLegacyGroups()
    {
        // 分组名是能力包与壳层的共同词汇（写进 NavPlacement.Group），不能改名
        NavGroups.Control.Should().Be("控制中心");
        NavGroups.Workspace.Should().Be("工作区");
        NavGroups.Settings.Should().Be("设置");
    }
}
