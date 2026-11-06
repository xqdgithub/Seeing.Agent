using FluentAssertions;
using Seeing.Serialization;
using System.Text.Json;
using Xunit;

namespace Seeing.Session.Tests.Serialization;

/// <summary>
/// <see cref="SeeingJson"/> 行为护栏：保证「中文可读」与「签名/哈希字节稳定」两个诉求
/// 不会在后续重构中被悄悄改掉。
/// </summary>
public sealed class SeeingJsonTests
{
    [Fact]
    public void Readable_WithChinese_ShouldNotEscape()
    {
        var json = JsonSerializer.Serialize(new { title = "新会话" }, SeeingJson.Readable);

        json.Should().Contain("新会话");
        json.Should().NotContain("\\u");
    }

    [Fact]
    public void Readable_WithQuotes_ShouldUseStandardJsonEscape()
    {
        // 必须是 \" 而不是 \u0022 —— 后者虽然合法，但对人不友好，也是本设计明确否掉的方案。
        var json = JsonSerializer.Serialize(new { text = "\"引号\"" }, SeeingJson.Readable);

        json.Should().Contain("\\\"引号\\\"");
        json.Should().NotContain("\\u0022");
    }

    [Fact]
    public void Readable_WithControlCharacters_ShouldStillEscape()
    {
        // 放开中文不等于放开控制字符：换行等必须仍然转义，否则产出非法 JSON。
        var json = JsonSerializer.Serialize(new { text = "a\nb\tc" }, SeeingJson.Readable);

        json.Should().Contain("\\n");
        json.Should().Contain("\\t");
        JsonDocument.Parse(json).RootElement.GetProperty("text").GetString().Should().Be("a\nb\tc");
    }

    [Fact]
    public void Readable_ShouldRoundTrip()
    {
        var original = new { title = "中文 <b>标签</b> & 符号", paths = new[] { "C:\\目录\\文件.txt" } };

        var json = JsonSerializer.Serialize(original, SeeingJson.Readable);
        var restored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, SeeingJson.Readable);

        restored!["title"].GetString().Should().Be(original.title);
        restored["paths"].EnumerateArray().Single().GetString().Should().Be(original.paths[0]);
    }

    [Fact]
    public void ReadableIndented_WithChinese_ShouldBeReadableAndIndented()
    {
        var json = JsonSerializer.Serialize(new { title = "新会话" }, SeeingJson.ReadableIndented);

        json.Should().Contain("新会话");
        json.Should().Contain("\n");
    }

    [Fact]
    public void Signature_WithChinese_ShouldKeepDefaultEscaping()
    {
        // 护栏：签名/哈希依赖字节稳定，必须继续输出 \uXXXX；改动它会静默作废既有签名。
        var json = JsonSerializer.Serialize(new { title = "新会话" }, SeeingJson.Signature);

        json.Should().Contain("\\u");
        json.Should().NotContain("新会话");
    }

    [Fact]
    public void Create_ShouldPreserveCallerSettings()
    {
        var options = SeeingJson.Create(indented: true, o =>
        {
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        options.Encoder.Should().BeSameAs(SeeingJson.ReadableEncoder);
        options.WriteIndented.Should().BeTrue();
        options.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);

        var json = JsonSerializer.Serialize(new { SomeName = "中文" }, options);
        json.Should().Contain("\"someName\"");
        json.Should().Contain("中文");
    }

    [Fact]
    public void Create_WithoutIndent_ShouldNotIndent()
    {
        var options = SeeingJson.Create(_ => { });

        options.WriteIndented.Should().BeFalse();
        options.Encoder.Should().BeSameAs(SeeingJson.ReadableEncoder);
    }
}
