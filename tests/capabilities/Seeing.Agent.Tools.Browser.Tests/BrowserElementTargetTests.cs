using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 元素定位判别：ref / selector / 坐标三选一，缺参与冲突必须给出可读原因。
/// <para>
/// 这些文案会直接回到模型，含糊的手误（同时给 ref 与坐标）会导致「点了但不知道按哪个」。
/// </para>
/// </summary>
public class BrowserElementTargetTests
{
    [Fact]
    public void Validate_Empty_ShouldExplainAllOptions()
    {
        var problem = new BrowserElementTarget().Validate();

        problem.Should().NotBeNull();
        problem.Should().Contain("ref").And.Contain("selector").And.Contain("坐标");
    }

    [Fact]
    public void ResolveSelector_Ref_ShouldBecomeAttributeSelector()
    {
        new BrowserElementTarget { Ref = "e2" }.ResolveSelector()
            .Should().Be("[data-seeing-ref=\"e2\"]");
    }

    [Theory]
    [InlineData("e1\"] , body")]
    [InlineData("body")]
    [InlineData("x9")]
    public void ResolveSelector_MalformedRef_ShouldThrowValidation(string reference)
    {
        // 唯一构造点做形状校验：任何把 ref 拼进选择器的路径都必须经过它
        // （browser_wait_for 的 kind=ref 曾完全绕过这条白名单）。
        var act = () => new BrowserElementTarget { Ref = reference }.ResolveSelector();

        act.Should().Throw<BrowserElementValidationException>()
            .WithMessage("*形状不合法*");
    }

    [Fact]
    public void ResolveSelector_RefTrimsWhitespace()
    {
        new BrowserElementTarget { Ref = "  e7 " }.ResolveSelector()
            .Should().Be("[data-seeing-ref=\"e7\"]");
    }

    [Fact]
    public void ResolveSelector_RefWins_OverSelector()
    {
        new BrowserElementTarget { Ref = "e1", Selector = "#other" }.ResolveSelector()
            .Should().Be("[data-seeing-ref=\"e1\"]");
    }

    [Fact]
    public void ResolveSelector_SelectorOnly_ShouldBeUsed()
    {
        new BrowserElementTarget { Selector = ".submit" }.ResolveSelector().Should().Be(".submit");
    }

    [Fact]
    public void Validate_RefAndCoordinates_ShouldReject()
    {
        var target = new BrowserElementTarget { Ref = "e1", X = 10, Y = 20 };

        target.Validate().Should().Contain("二选一");
    }

    [Fact]
    public void Validate_OnlyX_ShouldReject()
    {
        var target = new BrowserElementTarget { X = 10 };

        target.Validate().Should().Contain("x 与 y");
    }

    [Fact]
    public void Validate_CoordinatesOnly_ShouldPass()
    {
        new BrowserElementTarget { X = 10, Y = 20 }.Validate().Should().BeNull();
    }

    [Fact]
    public void Validate_RefOnly_ShouldPass()
    {
        new BrowserElementTarget { Ref = "e1" }.Validate().Should().BeNull();
    }

    [Theory]
    [InlineData("e1\"] , body")]
    [InlineData("e1'")]
    [InlineData("body")]
    [InlineData("x1")]
    [InlineData("e")]
    public void Validate_MalformedRef_ShouldReject(string reference)
    {
        // ref 会被拼进 CSS 属性选择器；不校验形状可注入选择器片段、
        // 命中快照之外的任意元素（「ref 只能命中快照打标元素」这条叙事就不成立）。
        var problem = new BrowserElementTarget { Ref = reference }.Validate();

        problem.Should().NotBeNull();
        problem.Should().Contain("形状不合法");
    }

    [Fact]
    public void ToString_ShouldPreferRefThenSelectorThenCoordinates()
    {
        new BrowserElementTarget { Ref = "e1" }.ToString().Should().Be("e1");
        new BrowserElementTarget { Selector = "#a" }.ToString().Should().Be("#a");
        new BrowserElementTarget { X = 1.5, Y = 2.5 }.ToString().Should().Be("(1.5, 2.5)");
        new BrowserElementTarget().ToString().Should().Be("(未指定)");
    }

    [Fact]
    public void WaitCondition_SelectorWithoutValue_ShouldReject()
    {
        var problem = new BrowserWaitCondition(BrowserWaitKind.Selector, null).Validate();

        problem.Should().NotBeNull();
        problem.Should().Contain("value");
    }

    [Fact]
    public void WaitCondition_NetworkIdleWithoutValue_ShouldPass()
    {
        new BrowserWaitCondition(BrowserWaitKind.NetworkIdle, null).Validate().Should().BeNull();
    }
}
