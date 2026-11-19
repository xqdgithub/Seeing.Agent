using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Browser;
using Seeing.Agent.Tools.Browser.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 浏览器工具的参数契约守门：类型不符/越界必须失败，不夹取、不回落默认；
/// 参数校验先于「有没有页面」；每个工具都有外层超时兜底。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这些用例重要</b>：布尔默认值往往带副作用（<c>clear=true</c> 会清空输入框），
/// 整数默认值会让「参数看着生效了」变成假象。这类静默降级比直接报错危险得多。
/// </para>
/// <para>
/// 所有用例都不需要真实浏览器：参数校验在取页面之前完成，空宿主足够。
/// </para>
/// </remarks>
public class BrowserToolParameterContractTests
{
    private static IBrowserSessionHost EmptyHost()
    {
        var host = new Mock<IBrowserSessionHost>();
        host.Setup(h => h.TryGetPage(It.IsAny<string>())).Returns((BrowserPageHandle?)null);
        host.Setup(h => h.HasPage(It.IsAny<string>())).Returns(false);
        return host.Object;
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static Task<ToolResult> RunAsync(ITool tool, string json)
        => tool.ExecuteAsync(Args(json), new ToolContext { SessionId = "s_contract" });

    private static BrowserTypeTool TypeTool()
        => new(NullLogger<BrowserTypeTool>.Instance, EmptyHost());

    private static BrowserEvalTool EvalTool()
        => new(NullLogger<BrowserEvalTool>.Instance, EmptyHost());

    private static BrowserScreenshotTool ScreenshotTool()
        => new(
            NullLogger<BrowserScreenshotTool>.Instance,
            EmptyHost(),
            new Mock<ISessionArtifactStore>().Object);

    private static BrowserClickTool ClickTool()
        => new(NullLogger<BrowserClickTool>.Instance, EmptyHost());

    private static BrowserSnapshotTool SnapshotTool()
        => new(NullLogger<BrowserSnapshotTool>.Instance, EmptyHost());

    private static BrowserScrollTool ScrollTool()
        => new(NullLogger<BrowserScrollTool>.Instance, EmptyHost());

    [Theory]
    [InlineData("""{"ref":"e1","text":"x","clear":"false"}""")]
    [InlineData("""{"ref":"e1","text":"x","submit":"true"}""")]
    public async Task Type_BooleanGivenAsString_ShouldFailInsteadOfUsingDefault(string json)
    {
        // clear/submit 的默认值分别是「清空」「不提交」——静默回落会让工具做出与意图相反的动作
        var result = await RunAsync(TypeTool(), json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("布尔值");
    }

    [Fact]
    public async Task Type_DelayOutOfRange_ShouldFail()
    {
        var result = await RunAsync(TypeTool(), """{"ref":"e1","text":"x","delayMs":2000}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("delayMs");
        result.Error.Should().Contain(BrowserInputLimits.MaxTypeDelayMs.ToString());
    }

    [Fact]
    public async Task Type_DelayGivenAsString_ShouldFail()
    {
        var result = await RunAsync(TypeTool(), """{"ref":"e1","text":"x","delayMs":"50"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("整数");
    }

    [Fact]
    public async Task Eval_AwaitPromiseGivenAsString_ShouldFail()
    {
        var result = await RunAsync(EvalTool(), """{"expression":"1","awaitPromise":"false"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("布尔值");
    }

    [Fact]
    public async Task Screenshot_FullPageGivenAsString_ShouldFail()
    {
        var result = await RunAsync(ScreenshotTool(), """{"fullPage":"true"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("布尔值");
    }

    [Theory]
    [InlineData("""{"ref":"e1","clickCount":5}""")]
    [InlineData("""{"ref":"e1","clickCount":0}""")]
    [InlineData("""{"ref":"e1","clickCount":"2"}""")]
    public async Task Click_InvalidClickCount_ShouldFail(string json)
    {
        var result = await RunAsync(ClickTool(), json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("clickCount");
    }

    [Theory]
    [InlineData("""{"maxElements":5000}""")]
    [InlineData("""{"maxElements":"300"}""")]
    public async Task Snapshot_InvalidMaxElements_ShouldFail(string json)
    {
        var result = await RunAsync(SnapshotTool(), json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("maxElements");
    }

    [Fact]
    public async Task Scroll_LegacyAmount_ShouldBeRejectedWithGuidance()
    {
        var result = await RunAsync(ScrollTool(), """{"amount":600}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("amountPx");
    }

    [Theory]
    [InlineData("""{"amountPx":-100}""")]
    [InlineData("""{"amountPx":99999}""")]
    [InlineData("""{"amountPx":"600"}""")]
    public async Task Scroll_InvalidAmountPx_ShouldFailInsteadOfScrollingWrongWay(string json)
    {
        // 负数曾被静默换成「向同方向滚 600」——方向错误比精度错误严重得多
        var result = await RunAsync(ScrollTool(), json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("amountPx");
    }

    [Fact]
    public async Task MissingRequiredArgument_ShouldNotBlameMissingPage()
    {
        // 参数错与「还没打开页面」无关：先报参数，模型才知道该改什么
        var type = await RunAsync(TypeTool(), "{}");
        var press = await RunAsync(new BrowserPressTool(NullLogger<BrowserPressTool>.Instance, EmptyHost()), "{}");
        var eval = await RunAsync(EvalTool(), "{}");

        type.Error.Should().Contain("text").And.NotContain("browser_open");
        press.Error.Should().Contain("key").And.NotContain("browser_open");
        eval.Error.Should().Contain("expression").And.NotContain("browser_open");
    }

    [Fact]
    public void AllBrowserTools_ShouldDeclareOuterTimeoutBudget()
    {
        var expected = new Dictionary<string, long>
        {
            ["browser_open"] = 130_000,
            ["browser_wait_for"] = 130_000,
            ["browser_eval"] = 120_000,
        };

        foreach (var tool in AllTools())
        {
            var value = tool.Capabilities[ToolCapabilityKeys.TimeoutBudget];
            long.Parse(value).Should().Be(
                expected.GetValueOrDefault(tool.Id, 60_000),
                $"{tool.Id} 必须有外层超时兜底；全局 ToolExecutionTimeout 默认关闭");

            tool.Capabilities.Should().NotContainKey(ToolCapabilityKeys.TimeoutSkip);
        }
    }

    private static IEnumerable<ITool> AllTools()
    {
        var host = EmptyHost();
        yield return new BrowserOpenTool(
            NullLogger<BrowserOpenTool>.Instance,
            host,
            new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()));
        yield return new BrowserSnapshotTool(NullLogger<BrowserSnapshotTool>.Instance, host);
        yield return new BrowserClickTool(NullLogger<BrowserClickTool>.Instance, host);
        yield return new BrowserTypeTool(NullLogger<BrowserTypeTool>.Instance, host);
        yield return new BrowserPressTool(NullLogger<BrowserPressTool>.Instance, host);
        yield return new BrowserScrollTool(NullLogger<BrowserScrollTool>.Instance, host);
        yield return new BrowserWaitForTool(NullLogger<BrowserWaitForTool>.Instance, host);
        yield return new BrowserScreenshotTool(
            NullLogger<BrowserScreenshotTool>.Instance,
            host,
            new Mock<ISessionArtifactStore>().Object);
        yield return new BrowserEvalTool(NullLogger<BrowserEvalTool>.Instance, host);
        yield return new BrowserBackTool(NullLogger<BrowserBackTool>.Instance, host);
        yield return new BrowserForwardTool(NullLogger<BrowserForwardTool>.Instance, host);
        yield return new BrowserCloseTool(NullLogger<BrowserCloseTool>.Instance, host);
    }
}
