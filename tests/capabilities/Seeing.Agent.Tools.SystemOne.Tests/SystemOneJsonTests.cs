using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneJsonTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Unwrap_普通文本_原样返回()
    {
        var element = Json("\"hello\"");

        var unwrapped = SystemOneJson.Unwrap(element);

        unwrapped.ValueKind.Should().Be(JsonValueKind.String);
        unwrapped.GetString().Should().Be("hello");
    }

    [Fact]
    public void Unwrap_数字文本_不解包为数字()
    {
        SystemOneJson.Unwrap(Json("\"42\"")).ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void Unwrap_字符串化对象_解包()
    {
        var unwrapped = SystemOneJson.Unwrap(Json("\"{\\\"a\\\":1}\""));

        unwrapped.ValueKind.Should().Be(JsonValueKind.Object);
        unwrapped.GetProperty("a").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Unwrap_双层字符串_递归解包()
    {
        var inner = "{\"a\":1}";
        var element = Json(JsonSerializer.Serialize(JsonSerializer.Serialize(inner)));

        SystemOneJson.Unwrap(element).ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void TryGetArray_字符串化数组_成功()
    {
        var ok = SystemOneJson.TryGetArray(Json("\"[1,2,3]\""), out var array);

        ok.Should().BeTrue();
        array.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void TryGetArray_对象_失败()
        => SystemOneJson.TryGetArray(Json("{\"a\":1}"), out _).Should().BeFalse();

    [Fact]
    public void TryGetObject_字符串化对象_成功()
    {
        var ok = SystemOneJson.TryGetObject(Json("\"{\\\"a\\\":1}\""), out var obj);

        ok.Should().BeTrue();
        obj.GetProperty("a").GetInt32().Should().Be(1);
    }

    [Fact]
    public void NormalizeState_文本_返回字符串()
        => SystemOneJson.NormalizeState(Json("\"text\"")).Should().Be("text");

    [Fact]
    public void NormalizeState_字符串化对象_返回JsonElement()
    {
        var state = SystemOneJson.NormalizeState(Json("\"{\\\"a\\\":1}\""));

        state.Should().BeOfType<JsonElement>();
        ((JsonElement)state!).GetProperty("a").GetInt32().Should().Be(1);
    }

    [Fact]
    public void NormalizeState_原生对象_返回JsonElement()
        => SystemOneJson.NormalizeState(Json("{\"a\":1}")).Should().BeOfType<JsonElement>();
}
