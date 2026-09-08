using FluentAssertions;
using Seeing.Agent.Memory.Core.Evolution;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Evolution;

public class LlmMemoryEvolutionJsonTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sorry, nothing to evolve")]
    public void ExtractJsonPayload_EmptyOrNonJson_ShouldReturnEmpty(string input)
    {
        LlmMemoryEvolution.ExtractJsonPayloadForTests(input).Should().BeEmpty();
    }

    [Fact]
    public void ExtractJsonPayload_WithPreamble_ShouldExtractObject()
    {
        var input = """
            Here is the result:
            {"items":[{"title":"t","content":"c","importance":0.9}]}
            """;

        var json = LlmMemoryEvolution.ExtractJsonPayloadForTests(input);
        json.Should().StartWith("{").And.EndWith("}");
        json.Should().Contain("\"items\"");
    }
}
