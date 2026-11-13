using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Hooks.Directory.Projection;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookPayloadProjectionTests
{
    [Fact]
    public void Project_UndefinedJsonElement_BecomesNull()
    {
        var bag = new Dictionary<string, object?> { ["args"] = default(JsonElement) };
        var json = HookPayloadProjection.Project(bag);
        json["args"]!.GetValueKind().Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Project_Exception_BecomesTypeAndMessage()
    {
        var json = HookPayloadProjection.Project(new Dictionary<string, object?>
        {
            ["error"] = new InvalidOperationException("boom")
        });
        json["error"]!["type"]!.GetValue<string>().Should().Be(typeof(InvalidOperationException).FullName);
        json["error"]!["message"]!.GetValue<string>().Should().Be("boom");
    }

    [Fact]
    public void Project_DropsLiveObjects()
    {
        var dropped = new List<string>();
        var json = HookPayloadProjection.Project(
            new Dictionary<string, object?> { ["token"] = new CancellationTokenSource() },
            dropped.Add);
        json.ContainsKey("token").Should().BeFalse();
        dropped.Should().Equal("token");
    }

    [Fact]
    public void TryWrite_JsonElement_Clones()
    {
        using var original = JsonDocument.Parse("{\"a\":1}");
        var bag = new Dictionary<string, object?> { ["args"] = original.RootElement };
        using var returned = JsonDocument.Parse("{\"args\":{\"b\":2}}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out var error).Should().BeTrue();
        error.Should().BeNull();
        var written = bag["args"].Should().BeOfType<JsonElement>().Subject;
        written.GetProperty("b").GetInt32().Should().Be(2);
        returned.Dispose();
        written.GetProperty("b").GetInt32().Should().Be(2);
    }

    [Fact]
    public void TryWrite_NullOntoUndefinedSlot_BecomesNullElement()
    {
        var bag = new Dictionary<string, object?> { ["args"] = default(JsonElement) };
        using var returned = JsonDocument.Parse("{\"args\":null}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        bag["args"].Should().BeOfType<JsonElement>().Which.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TryWrite_Headers_ReplacesWholeDictionary()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Keep"] = "1",
            ["Drop"] = "2"
        };
        var bag = new Dictionary<string, object?> { ["headers"] = headers };
        using var returned = JsonDocument.Parse("{\"headers\":{\"Keep\":\"9\"}}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        var written = bag["headers"].Should().BeAssignableTo<IDictionary<string, string>>().Subject;
        written.Should().NotContainKey("Drop");
        written["Keep"].Should().Be("9");
        ((Dictionary<string, string>)written).Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryWrite_MaxTokensFromNull_IsInt()
    {
        var bag = new Dictionary<string, object?> { ["maxTokens"] = null, ["temperature"] = null };
        using var returned = JsonDocument.Parse("{\"maxTokens\":128,\"temperature\":0.5}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        bag["maxTokens"].Should().Be(128);
        bag["temperature"].Should().Be(0.5d);
    }

    [Fact]
    public void TryWrite_InvalidValue_WritesNothing()
    {
        var bag = new Dictionary<string, object?> { ["proceed"] = true, ["arguments"] = "old" };
        using var returned = JsonDocument.Parse("{\"proceed\":\"no\",\"arguments\":\"new\"}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out var error).Should().BeFalse();
        error.Should().NotBeNull();
        bag["proceed"].Should().Be(true);
        bag["arguments"].Should().Be("old");
    }

    [Fact]
    public void TryWrite_AbsentKey_Stays()
    {
        var bag = new Dictionary<string, object?> { ["arguments"] = "old" };
        using var returned = JsonDocument.Parse("{}");
        HookPayloadProjection.TryWrite(bag, returned.RootElement, out _).Should().BeTrue();
        bag["arguments"].Should().Be("old");
    }
}
