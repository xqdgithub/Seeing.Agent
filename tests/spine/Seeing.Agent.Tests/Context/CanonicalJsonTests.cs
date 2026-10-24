using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Core.Context;
using Xunit;

namespace Seeing.Agent.Tests.Context;

public sealed class CanonicalJsonTests
{
    // 属性顺序不影响结果
    [Fact]
    public void Serialize_ShouldIgnorePropertyOrder()
    {
        using var a = JsonDocument.Parse("""{"b":1,"a":2}""");
        using var b = JsonDocument.Parse("""{"a":2,"b":1}""");

        CanonicalJson.Serialize(a.RootElement).Should().Be(CanonicalJson.Serialize(b.RootElement));
        CanonicalJson.Serialize(a.RootElement).Should().Be("""{"a":2,"b":1}""");
    }

    // 空白不影响结果
    [Fact]
    public void Serialize_ShouldStripWhitespace()
    {
        using var d = JsonDocument.Parse("{\n  \"a\" : [ 1 , 2 ]\n}");

        CanonicalJson.Serialize(d.RootElement).Should().Be("""{"a":[1,2]}""");
    }

    // 已知向量：规范化输出必须稳定（golden，回归即意味着 diff/持久化字节漂移）
    [Fact]
    public void Serialize_ShouldMatchGoldenVector()
    {
        using var d = JsonDocument.Parse("""{"a":[1,2],"b":"x"}""");

        CanonicalJson.Serialize(d.RootElement).Should().Be("""{"a":[1,2],"b":"x"}""");
    }
}
