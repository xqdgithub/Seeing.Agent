using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 快照模型与渲染：格式必须稳定（同一页面同一时刻两次渲染逐字节相等），
/// 否则上下文增量管道会误判「页面变了」并反复叙述。
/// </summary>
public class BrowserSnapshotTests
{
    private static BrowserSnapshot CreateSample() => new(
        Url: "https://example.com/",
        Title: "Example Domain",
        RefEpoch: 2,
        Elements:
        [
            new BrowserSnapshotElement("e1", "link", "More information...", []),
            new BrowserSnapshotElement("e2", "textbox", "搜索", ["required"]),
            new BrowserSnapshotElement("e3", "button", "提交", ["disabled"])
        ],
        TotalElements: 3,
        VisibleText: "示例文本");

    [Fact]
    public void Render_ShouldBeByteStable()
    {
        var snapshot = CreateSample();

        snapshot.Render().Should().Be(snapshot.Render());
    }

    [Fact]
    public void Render_ShouldContainTitleUrlAndElements()
    {
        var text = CreateSample().Render();

        text.Should().Contain("Example Domain");
        text.Should().Contain("https://example.com/");
        text.Should().Contain("e1 link \"More information...\"");
        text.Should().Contain("e2 textbox \"搜索\" (required)");
        text.Should().Contain("e3 button \"提交\" (disabled)");
        text.Should().Contain("可交互元素 3 个");
    }

    [Fact]
    public void Render_WithoutVisibleText_ShouldOmitSection()
    {
        var snapshot = CreateSample() with { VisibleText = null };

        snapshot.Render().Should().NotContain("可见文本摘要");
    }

    [Fact]
    public void Render_Truncated_ShouldMentionRemainingCount()
    {
        var snapshot = CreateSample() with
        {
            Elements = [new BrowserSnapshotElement("e1", "link", "a", [])],
            TotalElements = 42
        };

        var text = snapshot.Render();

        text.Should().Contain("另有 41 个元素未列出");
        snapshot.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Render_NoElements_ShouldSayNone()
    {
        var snapshot = CreateSample() with { Elements = [], TotalElements = 0, VisibleText = null };

        snapshot.Render().Should().Contain("可交互元素：无");
    }

    [Fact]
    public void Render_UntitledPage_ShouldFallBackToPlaceholder()
    {
        var snapshot = CreateSample() with { Title = "" };

        snapshot.Render().Should().Contain("(无标题)");
    }

    [Fact]
    public void ElementToLine_EmptyName_ShouldOmitQuotes()
    {
        new BrowserSnapshotElement("e1", "button", "", []).ToLine().Should().Be("e1 button");
    }

    [Fact]
    public void Parse_FromScriptPayload_ShouldMapAllFields()
    {
        var payload = System.Text.Json.JsonDocument.Parse("""
            {
              "url": "https://a.test/",
              "title": "A",
              "total": 5,
              "elements": [
                { "ref": "e1", "role": "link", "name": "x", "states": ["disabled", "checked"] },
                { "ref": "e2", "role": "button", "name": "y" }
              ],
              "text": "hello"
            }
            """).RootElement;

        var snapshot = BrowserSnapshotBuilder.Parse(payload, refEpoch: 7);

        snapshot.Url.Should().Be("https://a.test/");
        snapshot.Title.Should().Be("A");
        snapshot.RefEpoch.Should().Be(7);
        snapshot.TotalElements.Should().Be(5);
        snapshot.Elements.Should().HaveCount(2);
        snapshot.Elements[0].States.Should().Equal("disabled", "checked");
        snapshot.Elements[1].States.Should().BeEmpty();
        snapshot.VisibleText.Should().Be("hello");
    }

    [Fact]
    public void Parse_ElementWithoutRef_ShouldBeSkipped()
    {
        var payload = System.Text.Json.JsonDocument.Parse("""
            { "url": "u", "title": "t", "total": 2, "elements": [ { "role": "link" }, { "ref": "e2", "role": "button" } ] }
            """).RootElement;

        var snapshot = BrowserSnapshotBuilder.Parse(payload, refEpoch: 0);

        snapshot.Elements.Should().ContainSingle();
        snapshot.Elements[0].Ref.Should().Be("e2");
    }

    [Fact]
    public void Parse_EmptyPayload_ShouldNotThrow()
    {
        var payload = System.Text.Json.JsonDocument.Parse("{}").RootElement;

        var snapshot = BrowserSnapshotBuilder.Parse(payload, refEpoch: 1);

        snapshot.Url.Should().BeEmpty();
        snapshot.Elements.Should().BeEmpty();
        snapshot.TotalElements.Should().Be(0);
    }

    [Fact]
    public void BuildScript_ShouldClampMaxElements()
    {
        var script = BrowserSnapshotBuilder.BuildScript(maxElements: 99_999, includeText: true);

        script.Should().Contain($"MAX = {BrowserSnapshotBuilder.MaxElementsLimit}");
    }

    [Fact]
    public void BuildScript_IncludeTextFalse_ShouldEmitFalseLiteral()
    {
        var script = BrowserSnapshotBuilder.BuildScript(maxElements: 10, includeText: false);

        script.Should().Contain("INCLUDE_TEXT = false");
    }
}
