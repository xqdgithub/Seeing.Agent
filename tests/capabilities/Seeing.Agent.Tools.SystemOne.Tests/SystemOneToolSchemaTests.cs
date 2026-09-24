using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneToolSchemaTests
{
    private static SystemOneTool Create(SystemOneToolKind kind) =>
        new(kind, NullLogger<SystemOneTool>.Instance);

    [Theory]
    [InlineData(SystemOneToolKind.Ask, "systemone_ask")]
    [InlineData(SystemOneToolKind.Noul, "systemone_noul")]
    [InlineData(SystemOneToolKind.Choice, "systemone_choice")]
    [InlineData(SystemOneToolKind.Score, "systemone_score")]
    public void Id_应按kind(SystemOneToolKind kind, string expected)
        => Create(kind).Id.Should().Be(expected);

    [Fact]
    public void state类型_应支持string与object()
    {
        var type = Create(SystemOneToolKind.Noul).ParametersSchema
            .GetProperty("properties").GetProperty("state").GetProperty("type");

        type.ValueKind.Should().Be(JsonValueKind.Array);
        type.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("string", "object");
    }

    [Fact]
    public void questions类型_应支持array与string()
    {
        var type = Create(SystemOneToolKind.Noul).ParametersSchema
            .GetProperty("properties").GetProperty("questions").GetProperty("type");

        type.ValueKind.Should().Be(JsonValueKind.Array);
        type.EnumerateArray().Select(e => e.GetString())
            .Should().Equal("array", "string");
    }

    [Fact]
    public void 必填_应为state与questions()
    {
        Create(SystemOneToolKind.Ask).ParametersSchema
            .GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(new[] { "state", "questions" });
    }
}
