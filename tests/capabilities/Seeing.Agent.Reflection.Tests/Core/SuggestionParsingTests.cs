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
              {"kind":"skill","target":"有依据","description":"改代码前先构建","reason":"备注","evidence":["记录 A"]},
              {"kind":"skill","target":"无依据","description":"改代码前先构建","reason":"备注","evidence":[]}
            ]}
            """;

        var result = Suggestions.Parse(json);

        result.Should().HaveCount(1);
        result[0].Target.Should().Be("有依据");
        result[0].Description.Should().Be("改代码前先构建");
        result[0].Reason.Should().Be("备注");
    }

    [Fact]
    public void Parse_Should_Drop_Empty_Description()
    {
        const string json = """
            {"suggestions":[
              {"kind":"skill","target":"无描述","description":"   ","reason":"备注可有","evidence":["记录 A"]},
              {"kind":"skill","target":"有描述","description":"改代码前先构建","reason":"备注","evidence":["记录 B"]}
            ]}
            """;

        var result = Suggestions.Parse(json);

        result.Should().HaveCount(1);
        result[0].Target.Should().Be("有描述");
    }

    [Fact]
    public void Parse_Should_Allow_Missing_Reason()
    {
        // reason 是备注，缺了不拦；description 才是选型描述。
        const string json = """
            {"suggestions":[{"kind":"skill","target":"no-notes","description":"改代码前先构建","evidence":["记录 A"]}]}
            """;

        var result = Suggestions.Parse(json);

        result.Should().HaveCount(1);
        result[0].Description.Should().Be("改代码前先构建");
        result[0].Reason.Should().BeNull();
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
            {"suggestions":[{"kind":"skill","target":"fence-skill","description":"围栏内的选型描述","reason":"可复用","evidence":["记录 C"]}]}
            ```
            """;

        var result = Suggestions.Parse(text);

        result.Should().HaveCount(1);
        result[0].Target.Should().Be("fence-skill");
        result[0].Description.Should().Be("围栏内的选型描述");
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
            {"suggestions":[{"kind":"skill","target":"dedup","description":"去重选型","reason":"可复用","evidence":["乙","甲","乙","甲"]}]}
            """;

        var result = Suggestions.Parse(json);

        result.Should().HaveCount(1);
        result[0].Evidence.Should().Equal("乙", "甲");
    }

    [Fact]
    public void Parse_Should_Drop_Legacy_Reason_Only_Without_Description()
    {
        // 旧模型只写 reason、不写 description：整条丢弃，避免把会话回顾落成技能描述。
        const string json = """
            {"suggestions":[{"kind":"skill","target":"legacy","reason":"本会话中连续出现同类请求","evidence":["记录 A"]}]}
            """;

        Suggestions.Parse(json).Should().BeEmpty();
    }
}
