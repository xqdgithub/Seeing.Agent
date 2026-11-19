using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Tools.Browser;
using Seeing.Agent.Tools.Browser.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// browser_wait_for 的语义守门：timeout 是上限、duration 才是固定等待时长、
/// networkidle 预算用尽按就绪处理、工具自身豁免全局兜底超时。
/// </summary>
public class BrowserWaitForToolTests
{
    private static BrowserWaitForTool NewTool()
    {
        var host = new Mock<IBrowserSessionHost>();
        host.Setup(h => h.TryGetPage(It.IsAny<string>())).Returns((BrowserPageHandle?)null);
        return new BrowserWaitForTool(NullLogger<BrowserWaitForTool>.Instance, host.Object);
    }

    [Fact]
    public void Schema_ShouldExposeDurationAndTimeoutAsUpperBound()
    {
        using var doc = JsonDocument.Parse(NewTool().ParametersSchema.GetRawText());
        var properties = doc.RootElement.GetProperty("properties");

        properties.TryGetProperty("duration", out var duration).Should().BeTrue(
            "kind=timeout 的固定等待时长必须与上限分开，否则模型会把 timeout 当等待时长");
        duration.GetProperty("minimum").GetInt32().Should().Be(1);
        duration.GetProperty("maximum").GetInt32().Should().Be(300);

        var timeout = properties.GetProperty("timeout");
        timeout.GetProperty("description").GetString().Should().Contain("上限");
    }

    [Fact]
    public void Description_ShouldExplainDurationAndNetworkIdleBudget()
    {
        var description = NewTool().Description;
        description.Should().Contain("duration");
        description.Should().Contain("上限");
        description.Should().Contain("按就绪返回");
    }

    [Fact]
    public void Capabilities_ShouldSkipGlobalTimeout()
    {
        NewTool().Capabilities.Should().ContainKey("timeout.skip")
            .WhoseValue.Should().Be("true",
                "参数 timeout 已自行有界（1–300s），全局兜底超时会先取消并伪装成「等待超时」");
    }

    [Fact]
    public void WaitResult_ReadyAfterBudget_ShouldBeUsable()
    {
        var result = BrowserWaitResult.ReadyAfterBudget;
        result.Satisfied.Should().BeTrue();
        result.BudgetExhausted.Should().BeTrue();

        BrowserWaitResult.Ready.BudgetExhausted.Should().BeFalse();
    }
}
