using FluentAssertions;
using Seeing.Agent.WebUI.Helpers;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Helpers;

public class ToolCallDisplayFormatterTests
{
    [Fact]
    public void TryParseParameters_Object_ShouldReturnNameAndValues()
    {
        // Raw string: \\ in JSON → one backslash in the parsed path
        var json = """{"filePath":"E:\\a.cs","content":"hello"}""";

        var ok = ToolCallDisplayFormatter.TryParseParameters(json, out var entries);

        ok.Should().BeTrue();
        entries.Should().HaveCount(2);
        entries[0].Name.Should().Be("filePath");
        entries[0].Value.Should().Be(@"E:\a.cs");
        entries[1].Name.Should().Be("content");
        entries[1].Value.Should().Be("hello");
        entries[1].IsLong.Should().BeFalse();
    }

    [Fact]
    public void TryParseParameters_NestedObject_ShouldSerializeCompactJson()
    {
        var json = """{"meta":{"a":1}}""";

        ToolCallDisplayFormatter.TryParseParameters(json, out var entries);

        entries.Should().ContainSingle();
        entries[0].Name.Should().Be("meta");
        entries[0].Value.Should().Be("""{"a":1}""");
    }

    [Fact]
    public void TryParseParameters_EmptyObject_ShouldReturnFalse()
    {
        ToolCallDisplayFormatter.TryParseParameters("{}", out var entries)
            .Should().BeFalse();
        entries.Should().BeEmpty();
    }

    [Fact]
    public void TryParseParameters_InvalidJson_ShouldReturnFalse()
    {
        ToolCallDisplayFormatter.TryParseParameters("not-json", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLongValue_Over400Chars_ShouldBeTrue()
    {
        ToolCallDisplayFormatter.IsLongValue(new string('x', 401))
            .Should().BeTrue();
    }

    [Fact]
    public void IsLongValue_Over8Lines_ShouldBeTrue()
    {
        var value = string.Join("\n", Enumerable.Range(1, 9).Select(i => $"line{i}"));
        ToolCallDisplayFormatter.IsLongValue(value).Should().BeTrue();
    }

    [Fact]
    public void FormatJsonPretty_ValidObject_ShouldIndent()
    {
        var pretty = ToolCallDisplayFormatter.FormatJsonPretty("""{"a":1}""");
        pretty.Should().Contain("\n");
        pretty.Should().Contain("\"a\"");
    }

    [Fact]
    public void FormatJsonPretty_Invalid_ShouldReturnOriginal()
    {
        ToolCallDisplayFormatter.FormatJsonPretty("plain")
            .Should().Be("plain");
    }

    [Fact]
    public void FormatReadableContent_Json_ShouldPrettyPrint()
    {
        var readable = ToolCallDisplayFormatter.FormatReadableContent("""{"ok":true}""");
        readable.Should().Contain("\n");
    }

    [Fact]
    public void FormatReadableContent_PlainText_ShouldReturnAsIs()
    {
        ToolCallDisplayFormatter.FormatReadableContent("新文件已创建。")
            .Should().Be("新文件已创建。");
    }

    [Fact]
    public void FormatKeyValueCopyText_ShouldJoinLines()
    {
        var entries = new[]
        {
            new ToolCallParameterEntry("filePath", @"E:\a.cs", false),
            new ToolCallParameterEntry("content", "hi", false)
        };

        ToolCallDisplayFormatter.FormatKeyValueCopyText(entries)
            .Should().Be("filePath: E:\\a.cs\ncontent: hi");
    }
}
