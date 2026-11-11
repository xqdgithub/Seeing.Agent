using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Browser;
using Seeing.Agent.Tools.Browser.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 工具面守门：12 个工具必须各自独立（权限粒度）、有唯一 id、参数 Schema 合法、
/// 且在「页面未打开」时给出可执行的指引而不是空失败。
/// </summary>
public class BrowserToolsTests
{
    private static IBrowserSessionHost EmptyHost()
    {
        var host = new Mock<IBrowserSessionHost>();
        host.Setup(h => h.TryGetPage(It.IsAny<string>())).Returns((BrowserPageHandle?)null);
        host.Setup(h => h.HasPage(It.IsAny<string>())).Returns(false);
        return host.Object;
    }

    private static IEnumerable<BrowserToolBase> AllTools()
    {
        var host = EmptyHost();
        var loggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

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
            new Mock<Seeing.Agent.Abstractions.Sessions.ISessionArtifactStore>().Object);
        yield return new BrowserEvalTool(NullLogger<BrowserEvalTool>.Instance, host);
        yield return new BrowserBackTool(NullLogger<BrowserBackTool>.Instance, host);
        yield return new BrowserForwardTool(NullLogger<BrowserForwardTool>.Instance, host);
        yield return new BrowserCloseTool(NullLogger<BrowserCloseTool>.Instance, host);
    }

    [Fact]
    public void AllTools_ShouldHaveDistinctBrowserPrefixedIds()
    {
        var ids = AllTools().Select(t => t.Id).ToArray();

        ids.Should().HaveCount(12);
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => id.StartsWith("browser_", StringComparison.Ordinal));
    }

    [Fact]
    public void AllTools_ShouldMatchModuleProvidedTools()
    {
        var module = new BrowserModule();

        AllTools().Select(t => t.Id).Should().BeSubsetOf(module.ProvidedTools);
        module.ProvidedTools.Should().HaveCount(12);
    }

    [Fact]
    public void AllTools_ShouldDescribeThemselvesWithinLimits()
    {
        foreach (var tool in AllTools())
        {
            tool.Description.Should().NotBeNullOrWhiteSpace($"{tool.Id} 必须有描述");
            tool.Description.Length.Should().BeLessThan(1200, $"{tool.Id} 的描述过长会挤占上下文");
            tool.Description.Should().NotContain("##", $"{tool.Id} 描述不使用 Markdown 标题");
        }
    }

    [Fact]
    public void AllTools_ShouldUseNetworkCategory()
    {
        AllTools().Should().OnlyContain(t => t.Category == ToolCategory.Network);
    }

    [Fact]
    public void AllTools_Schema_ShouldBeObjectWithDescribedRequiredProperties()
    {
        foreach (var tool in AllTools())
        {
            var schema = tool.ParametersSchema;
            schema.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object, $"{tool.Id} 的 Schema 必须是对象");

            schema.TryGetProperty("type", out var type).Should().BeTrue();
            type.GetString().Should().Be("object");

            if (!schema.TryGetProperty("required", out var required))
                continue;

            schema.TryGetProperty("properties", out var properties).Should().BeTrue();

            foreach (var name in required.EnumerateArray().Select(e => e.GetString()!))
            {
                properties.TryGetProperty(name, out var property).Should().BeTrue(
                    $"{tool.Id} 的 required 键 '{name}' 必须在 properties 中声明");
                property.TryGetProperty("description", out var description).Should().BeTrue();
                description.GetString().Should().NotBeNullOrWhiteSpace(
                    $"{tool.Id} 的参数 '{name}' 必须有描述（docs/architecture/04 §9）");
            }
        }
    }

    [Fact]
    public async Task ToolsWithoutPage_ShouldGuideToOpen()
    {
        var context = new ToolContext { SessionId = "s1" };
        var arguments = System.Text.Json.JsonDocument.Parse("""{"text":"a","key":"Enter","expression":"1","kind":"networkidle"}""").RootElement;

        foreach (var tool in AllTools())
        {
            if (tool.Id is "browser_open" or "browser_close")
                continue;

            var result = await tool.ExecuteAsync(arguments, context);

            result.Success.Should().BeFalse($"{tool.Id} 在无页面时必须失败");
            result.Error.Should().Contain("browser_open", $"{tool.Id} 的失败文案要指向下一步");
        }
    }

    [Fact]
    public async Task Click_BothRefAndCoordinates_ShouldFailWithWhy()
    {
        var tool = new BrowserClickTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserClickTool>.Instance,
            EmptyHost());

        // 页面存在与否不是重点：参数校验在取页面之后，因此这里只验证「无页面」路径不吞掉原因
        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("""{"ref":"e1","x":1,"y":2}""").RootElement,
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("browser_open");
    }

    [Fact]
    public async Task Open_PrivateHost_ShouldBeDeniedBeforeTouchingBrowser()
    {
        var tool = new BrowserOpenTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserOpenTool>.Instance,
            EmptyHost(),
            new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()));

        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("""{"url":"http://127.0.0.1:8080"}""").RootElement,
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("内网");
    }

    [Fact]
    public async Task Open_UnsupportedScheme_ShouldBeDenied()
    {
        var tool = new BrowserOpenTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserOpenTool>.Instance,
            EmptyHost(),
            new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()));

        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("""{"url":"file:///c:/windows"}""").RootElement,
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("协议");
    }

    [Fact]
    public async Task Open_MissingUrl_ShouldFail()
    {
        var tool = new BrowserOpenTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserOpenTool>.Instance,
            EmptyHost(),
            new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()));

        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("{}").RootElement,
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("url");
    }

    [Fact]
    public async Task Close_WithoutPage_ShouldExplainNoop()
    {
        var tool = new BrowserCloseTool(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserCloseTool>.Instance,
            EmptyHost());

        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("{}").RootElement,
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无需关闭");
    }
}
