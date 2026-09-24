using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneToolParserTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 同类choice_解析_应成功且无type()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"q","options":[{"label":"x"},{"label":"y"}]}]}""");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Choice, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions[0].Type.Should().Be("choice");
        input.Questions[0].Options.Should().HaveCount(2);
    }

    [Fact]
    public void 同类noul_缺id_应自动生成()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"instructions":"a"},{"instructions":"b"}]}""");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions.Select(q => q.Id).Should().Equal("q0", "q1");
    }

    [Fact]
    public void ask混合_解析_应保留各自type()
    {
        var args = Json("""
        {"state":{"text":"s"},"questions":[
          {"id":"n","type":"noul","instructions":"x"},
          {"id":"c","type":"choice","instructions":"x","options":[{"label":"a"},{"label":"b"}]},
          {"id":"s","type":"score","instructions":"x","levels":["lo","hi"]}
        ]}
        """);

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Ask, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions.Select(q => q.Type).Should().Equal("noul", "choice", "score");
    }

    [Fact]
    public void ask_缺type_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x"}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Ask, out _, out var error).Should().BeFalse();
        error.Should().Contain("type");
    }

    [Fact]
    public void state为对象_应接受并保留结构()
    {
        var args = Json("""{"state":{"ticket":"x"},"questions":[{"instructions":"a"}]}""");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out var input, out var error);

        ok.Should().BeTrue(error);
        input.State.Should().BeOfType<JsonElement>();
        ((JsonElement)input.State!).GetProperty("ticket").GetString().Should().Be("x");
    }

    [Fact]
    public void state为纯文本_应保留为字符串()
    {
        var args = Json("""{"state":"plain text","questions":[{"instructions":"a"}]}""");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out var input, out var error);

        ok.Should().BeTrue(error);
        input.State.Should().Be("plain text");
    }

    [Fact]
    public void state为JSON字符串对象_应解包为结构()
    {
        var args = Json("""{"state":"{\"ticket\":\"x\"}","questions":[{"instructions":"a"}]}""");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out var input, out var error);

        ok.Should().BeTrue(error);
        input.State.Should().BeOfType<JsonElement>();
        ((JsonElement)input.State!).GetProperty("ticket").GetString().Should().Be("x");
    }

    [Fact]
    public void 同类choice_缺options_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x"}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Choice, out _, out var error).Should().BeFalse();
        error.Should().Contain("options");
    }

    [Fact]
    public void 同类choice_重复label_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x","options":[{"label":"d"},{"label":"d"}]}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Choice, out _, out var error).Should().BeFalse();
        error.Should().Contain("重复 label");
    }

    [Fact]
    public void 同类score_级别不足_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x","levels":["only"]}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Score, out _, out var error).Should().BeFalse();
        error.Should().Contain("levels");
    }

    [Fact]
    public void 同类noul_带options_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x","options":[{"label":"o"}]}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out _, out var error).Should().BeFalse();
        error.Should().Contain("noul");
    }

    [Fact]
    public void 同类noul_带levels_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x","levels":["a","b"]}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out _, out var error).Should().BeFalse();
        error.Should().Contain("noul");
    }

    [Fact]
    public void 同类score_含非字符串level_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x","levels":["a",1]}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Score, out _, out var error).Should().BeFalse();
        error.Should().Contain("字符串");
    }

    [Fact]
    public void 重复id_应失败()
    {
        var args = Json("""{"state":{"text":"s"},"questions":[{"id":"a","instructions":"x"},{"id":"a","instructions":"y"}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out _, out var error).Should().BeFalse();
        error.Should().Contain("重复");
    }

    [Fact]
    public void 题数超限_应失败()
    {
        var questions = string.Join(
            ",",
            Enumerable.Range(0, 21).Select(i => $$"""{"instructions":"x{{i}}"}"""));
        var args = Json($$"""{"state":{"text":"s"},"questions":[{{questions}}]}""");

        SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out _, out var error).Should().BeFalse();
        error.Should().Contain("超限");
    }

    [Fact]
    public void questions为JSON字符串_应解析()
    {
        var args = Json("""{"state":"text","questions":"[{\"id\":\"a\",\"instructions\":\"q\",\"options\":[{\"label\":\"x\"},{\"label\":\"y\"}]}]"}""");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Choice, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions.Should().HaveCount(1);
        input.Questions[0].Options.Should().HaveCount(2);
    }

    [Fact]
    public void questions为双层JSON字符串_应解析()
    {
        var array = "[{\"id\":\"a\",\"instructions\":\"q\"}]";
        var twiceEncoded = JsonSerializer.Serialize(JsonSerializer.Serialize(array));
        var args = Json($"{{\"state\":\"s\",\"questions\":{twiceEncoded}}}");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions.Should().HaveCount(1);
        input.Questions[0].Id.Should().Be("a");
    }

    [Fact]
    public void question项为JSON字符串_应解析()
    {
        var item = JsonSerializer.Serialize("{\"id\":\"a\",\"instructions\":\"q\"}");
        var args = Json($"{{\"state\":\"s\",\"questions\":[{item}]}}");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Noul, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions[0].Id.Should().Be("a");
    }

    [Fact]
    public void options为JSON字符串_应解析()
    {
        var options = JsonSerializer.Serialize("[{\"label\":\"x\"},{\"label\":\"y\"}]");
        var args = Json($"{{\"state\":\"s\",\"questions\":[{{\"id\":\"a\",\"instructions\":\"q\",\"options\":{options}}}]}}");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Choice, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions[0].Options.Should().HaveCount(2);
    }

    [Fact]
    public void levels为JSON字符串_应解析()
    {
        var levels = JsonSerializer.Serialize("[\"lo\",\"hi\"]");
        var args = Json($"{{\"state\":\"s\",\"questions\":[{{\"id\":\"a\",\"instructions\":\"q\",\"levels\":{levels}}}]}}");

        var ok = SystemOneToolParser.TryParse(args, SystemOneToolKind.Score, out var input, out var error);

        ok.Should().BeTrue(error);
        input.Questions[0].Levels.Should().Equal("lo", "hi");
    }
}
