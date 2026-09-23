using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneAskToolSchemaTests
{
    private static SystemOneAskTool CreateTool() => new(NullLogger<SystemOneAskTool>.Instance);

    [Fact]
    public void Id与Category_应正确()
    {
        var tool = CreateTool();

        tool.Id.Should().Be("systemone_ask");
        tool.Category.Should().Be(ToolCategory.ExternalService);
    }

    [Fact]
    public void Schema_应声明state与questions且都必需()
    {
        var schema = CreateTool().ParametersSchema;

        schema.GetProperty("type").GetString().Should().Be("object");
        var props = schema.GetProperty("properties");
        props.TryGetProperty("state", out _).Should().BeTrue();
        props.TryGetProperty("questions", out var questions).Should().BeTrue();
        questions.GetProperty("type").GetString().Should().Be("array");

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        required.Should().Contain(new[] { "state", "questions" });
    }

    [Fact]
    public void Schema_questions项_应含三题型枚举与相关字段()
    {
        var props = CreateTool().ParametersSchema
            .GetProperty("properties").GetProperty("questions")
            .GetProperty("items").GetProperty("properties");

        var typeEnum = props.GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        typeEnum.Should().BeEquivalentTo(new[] { "noul", "choice", "score" });

        props.TryGetProperty("options", out _).Should().BeTrue();
        props.TryGetProperty("levels", out _).Should().BeTrue();
        props.TryGetProperty("criteriaTrue", out _).Should().BeTrue();
        props.TryGetProperty("criteriaFalse", out _).Should().BeTrue();
    }
}
