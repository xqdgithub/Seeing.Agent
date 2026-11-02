using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>建议解析：容错、丢弃规则与去重。</summary>
public sealed class SuggestionParsingTests
{
    [Fact]
    public void Parse_Should_Drop_Empty_Evidence()
    {
        const string json = """
            {"suggestions":[
              {"kind":"skill","target":"有依据","reason":"因为可复用","evidence":["记录 A"]},
              {"kind":"skill","target":"无依据","reason":"因为可复用","evidence":[]}
            ]}
            """;

        var result = Suggestions.Parse(json);

        result.Should().HaveCount(1);
        result[0].Target.Should().Be("有依据");
    }

    [Fact]
    public void Parse_Should_Drop_Empty_Reason()
    {
        const string json = """
            {"suggestions":[
              {"kind":"skill","target":"无原因","reason":"   ","evidence":["记录 A"]},
              {"kind":"skill","target":"有原因","reason":"确实可复用","evidence":["记录 B"]}
            ]}
            """;

        var result = Suggestions.Parse(json);

        result.Should().HaveCount(1);
        result[0].Target.Should().Be("有原因");
    }

    [Fact]
    public void Parse_Should_Handle_Skip_Action()
    {
        var result = Suggestions.Parse("""{"action":"skip"}""");

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Should_Tolerate_Json_Fence()
    {
        const string text = """
            下面是建议：

            ```json
            {"suggestions":[{"kind":"skill","target":"围栏内的","reason":"可复用","evidence":["记录 C"]}]}
            ```
            """;

        var result = Suggestions.Parse(text);

        result.Should().HaveCount(1);
        result[0].Target.Should().Be("围栏内的");
    }

    [Fact]
    public void Parse_Should_Return_Empty_On_Invalid_Json()
    {
        // 后台链路：非法输入必须降级为空，不得抛异常。
        var result = Suggestions.Parse("{ this is not json ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Should_Return_Empty_On_Null_Or_Blank()
    {
        Suggestions.Parse(null).Should().BeEmpty();
        Suggestions.Parse("   ").Should().BeEmpty();
        Suggestions.Parse(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Parse_Should_Dedup_Evidence_Preserving_Order()
    {
        const string json = """
            {"suggestions":[{"kind":"skill","target":"去重","reason":"可复用","evidence":["乙","甲","乙","甲"]}]}
            """;

        var result = Suggestions.Parse(json);

        result.Should().HaveCount(1);
        result[0].Evidence.Should().Equal("乙", "甲");
    }
}
