using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Xunit;

namespace Seeing.Agent.Tests.Ui;

public class EnvelopeContractTests
{
    [Fact]
    public void PageContribution_WithNullNav_IsPureRoute()
    {
        var page = new PageContribution
        {
            Point = "shell.page", Id = "p", Route = "/x", Title = "X", ViewKey = "p"
        };

        page.Nav.Should().BeNull();
        page.Requires.Should().BeEmpty();
        page.Order.Should().Be(0);
    }

    [Fact]
    public void UiContribution_HasNoComponentType()
    {
        // D1 的锚点：契约层不得出现 Blazor 组件类型
        typeof(UiContribution).GetProperties()
            .Select(p => p.PropertyType)
            .Should().NotContain(typeof(Type), "契约层不得携带组件类型");

        typeof(PageContribution).GetProperties()
            .Select(p => p.PropertyType)
            .Should().NotContain(typeof(Type));
    }

    [Fact]
    public void UiContribution_HasNoModuleId()
    {
        // 归属由注册表条目持有（E3）
        typeof(UiContribution).GetProperty("ModuleId").Should().BeNull();
    }

    [Fact]
    public void UiContribution_HasNoScenarios()
    {
        // 全仓零使用，已删除（E7）
        typeof(UiContribution).GetProperty("Scenarios").Should().BeNull();
        typeof(PageContribution).GetProperty("Scenarios").Should().BeNull();
    }

    [Fact]
    public void WithExpression_PreservesDerivedType()
    {
        // AddRange 用 with 重建时必须保留派生类型
        UiContribution page = new PageContribution
        { Point = "shell.page", Id = "p", Route = "/x", Title = "X", ViewKey = "p" };

        var stamped = page with { Order = 5 };

        stamped.Should().BeOfType<PageContribution>();
    }

    [Fact]
    public void IUiContributor_DescribeUi_ShouldBeAbstractNotDefault()
    {
        // 单成员接口的 DIM 等于 marker，且 Moq 会返回 default 而非走默认实现
        var method = typeof(IUiContributor).GetMethod(nameof(IUiContributor.DescribeUi))!;

        method.IsAbstract.Should().BeTrue("DescribeUi 必须是抽象成员");
    }

    [Fact]
    public void IUiHostContributor_RequiresHostPrefixInDocs()
    {
        // 契约约定：OwnerId 必须以 host. 开头（Collector 强制）
        typeof(IUiHostContributor).GetProperty(nameof(IUiHostContributor.OwnerId))
            .Should().NotBeNull();
    }

    [Fact]
    public void UiDiagnosticKind_HasCrossOwnerDuplicate()
    {
        Enum.IsDefined(typeof(UiDiagnosticKind), UiDiagnosticKind.CrossOwnerDuplicate)
            .Should().BeTrue();
    }
}
