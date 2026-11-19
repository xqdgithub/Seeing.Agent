using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Browser;
using Seeing.Agent.Tools.Browser.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// browser_wait_for 的语义守门：时长一律毫秒、越界必须失败而不是夹取、
/// networkidle 预算用尽按就绪处理、外层超时由 timeout.budget 兜住。
/// </summary>
public class BrowserWaitForToolTests
{
    private static BrowserWaitForTool NewTool()
    {
        var host = new Mock<IBrowserSessionHost>();
        host.Setup(h => h.TryGetPage(It.IsAny<string>())).Returns((BrowserPageHandle?)null);
        return new BrowserWaitForTool(NullLogger<BrowserWaitForTool>.Instance, host.Object);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static Task<ToolResult> RunAsync(string json)
        => NewTool().ExecuteAsync(Args(json), new ToolContext { SessionId = "s_wait" });

    [Fact]
    public void Schema_ShouldExposeMillisecondFieldsOnly()
    {
        using var doc = JsonDocument.Parse(NewTool().ParametersSchema.GetRawText());
        var properties = doc.RootElement.GetProperty("properties");

        properties.TryGetProperty("timeoutMs", out var timeoutMs).Should().BeTrue(
            "时长参数必须自带毫秒单位，避免调用方按秒/毫秒两种心智填值");
        timeoutMs.GetProperty("minimum").GetInt32().Should().Be(BrowserWaitForTool.MinWaitMs);
        timeoutMs.GetProperty("maximum").GetInt32().Should().Be(BrowserWaitForTool.MaxWaitMs);
        timeoutMs.GetProperty("description").GetString().Should().Contain("毫秒");

        properties.TryGetProperty("durationMs", out var durationMs).Should().BeTrue(
            "kind=timeout 的固定等待时长必须与上限分开，否则模型会把 timeout 当等待时长");
        durationMs.GetProperty("description").GetString().Should().Contain("毫秒");

        properties.TryGetProperty("timeout", out _).Should().BeFalse(
            "不带单位且语义双关的旧参数不得继续暴露");
        properties.TryGetProperty("duration", out _).Should().BeFalse();
    }

    [Fact]
    public void Description_ShouldExplainMillisecondsAndNetworkIdleBudget()
    {
        var description = NewTool().Description;
        description.Should().Contain("毫秒");
        description.Should().Contain("timeoutMs");
        description.Should().Contain("按就绪返回");
    }

    [Fact]
    public void Capabilities_ShouldBoundGlobalTimeoutInsteadOfSkipping()
    {
        var capabilities = NewTool().Capabilities;

        capabilities.Should().NotContainKey("timeout.skip",
            "完全豁免全局超时会让一次等待无上限地占住会话互斥");
        capabilities.Should().ContainKey("timeout.budget")
            .WhoseValue.Should().Be("130000",
                "预算 = 自身上限 120000ms + 收尾开销；越界仍有外层兜底");
    }

    [Fact]
    public async Task LegacySecondsParameter_ShouldBeRejectedWithGuidance()
    {
        var result = await RunAsync("""{"kind":"networkidle","timeout":15000}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("timeoutMs");
        result.Error.Should().Contain("毫秒");
    }

    [Theory]
    [InlineData("""{"kind":"selector","value":"#x","timeoutMs":15}""")]
    [InlineData("""{"kind":"networkidle","timeoutMs":900000}""")]
    public async Task OutOfRangeTimeout_ShouldFailInsteadOfClamping(string json)
    {
        var result = await RunAsync(json);

        result.Success.Should().BeFalse(
            "静默夹取会把 15000（本意 15 秒）放大成上限值，必须显式失败");
        result.Error.Should().Contain("毫秒");
    }

    [Fact]
    public async Task DurationBeyondTimeout_ShouldFail()
    {
        var result = await RunAsync("""{"kind":"timeout","timeoutMs":1000,"durationMs":5000}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("durationMs");
        result.Error.Should().Contain("不得超过");
    }

    [Fact]
    public async Task DurationOnOtherKinds_ShouldFail()
    {
        var result = await RunAsync("""{"kind":"selector","value":"#x","durationMs":500}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("仅用于 kind=timeout");
    }

    [Fact]
    public async Task NonIntegerMilliseconds_ShouldFailInsteadOfFallingBackToDefault()
    {
        var result = await RunAsync("""{"kind":"networkidle","timeoutMs":"5000"}""");

        result.Success.Should().BeFalse(
            "类型不对的时长不能被当成「未提供」而静默回落到默认预算");
        result.Error.Should().Contain("整数毫秒");
    }

    [Fact]
    public async Task ValidArguments_ShouldReachPageLookup()
    {
        // 参数合法时才会去取页面；没有页面时给出下一步指引
        var result = await RunAsync("""{"kind":"networkidle","timeoutMs":5000}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("browser_open");
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
