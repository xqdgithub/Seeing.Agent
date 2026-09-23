using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneAskParserTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 解析_三题型_应成功且映射字段()
    {
        var args = Json("""
        {
          "state": "hello",
          "questions": [
            { "id": "a", "type": "noul", "instructions": "is urgent?", "criteriaTrue": "yes", "criteriaFalse": "no" },
            { "id": "b", "type": "choice", "instructions": "which team", "options": [ { "label": "billing" }, { "label": "tech", "description": "bugs" } ] },
            { "id": "c", "type": "score", "instructions": "frustration", "levels": [ "calm", "angry" ] }
          ]
        }
        """);

        var ok = SystemOneAskParser.TryParse(args, out var input, out var error);

        ok.Should().BeTrue(error);
        input.State.Should().Be("hello");
        input.Questions.Should().HaveCount(3);
        input.Questions[0].Type.Should().Be("noul");
        input.Questions[0].CriteriaTrue.Should().Be("yes");
        input.Questions[1].Options.Should().HaveCount(2);
        input.Questions[1].Options[1].Description.Should().Be("bugs");
        input.Questions[2].Levels.Should().Equal("calm", "angry");
    }

    [Theory]
    [InlineData("""{"state":"s","questions":[]}""", "至少")]
    [InlineData("""{"questions":[{"id":"a","type":"noul","instructions":"x"}]}""", "state")]
    [InlineData("""{"state":"s"}""", "questions")]
    [InlineData("""{"state":"s","questions":[{"id":"","type":"noul","instructions":"x"}]}""", "id")]
    [InlineData("""{"state":"s","questions":[{"id":"a","type":"bogus","instructions":"x"}]}""", "type")]
    [InlineData("""{"state":"s","questions":[{"id":"a","type":"noul"}]}""", "instructions")]
    [InlineData("""{"state":"s","questions":[{"id":"a","type":"choice","instructions":"x"}]}""", "options")]
    [InlineData("""{"state":"s","questions":[{"id":"a","type":"score","instructions":"x","levels":["only"]}]}""", "levels")]
    [InlineData("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x","options":[{"label":"o"}]}]}""", "noul")]
    public void 解析_非法输入_应失败并给出原因(string json, string expectedFragment)
    {
        var ok = SystemOneAskParser.TryParse(Json(json), out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain(expectedFragment);
    }

    [Fact]
    public void 解析_重复id_应失败()
    {
        var args = Json("""{"state":"s","questions":[{"id":"a","type":"noul","instructions":"x"},{"id":"a","type":"noul","instructions":"y"}]}""");

        SystemOneAskParser.TryParse(args, out _, out var error).Should().BeFalse();
        error.Should().Contain("重复");
    }

    [Fact]
    public void 解析_choice重复label_应失败()
    {
        var args = Json("""{"state":"s","questions":[{"id":"a","type":"choice","instructions":"x","options":[{"label":"dup"},{"label":"dup"}]}]}""");

        SystemOneAskParser.TryParse(args, out _, out var error).Should().BeFalse();
        error.Should().Contain("重复 label");
    }

    [Fact]
    public void 解析_score含非字符串level_应失败()
    {
        var args = Json("""{"state":"s","questions":[{"id":"a","type":"score","instructions":"x","levels":["calm",1]}]}""");

        SystemOneAskParser.TryParse(args, out _, out var error).Should().BeFalse();
        error.Should().Contain("字符串");
    }

    [Fact]
    public void 解析_题数超限_应失败()
    {
        var questions = string.Join(
            ",",
            Enumerable.Range(0, 21).Select(i => $$"""{"id":"q{{i}}","type":"noul","instructions":"x"}"""));
        var args = Json($$"""{"state":"s","questions":[{{questions}}]}""");

        SystemOneAskParser.TryParse(args, out _, out var error).Should().BeFalse();
        error.Should().Contain("超限");
    }
}
