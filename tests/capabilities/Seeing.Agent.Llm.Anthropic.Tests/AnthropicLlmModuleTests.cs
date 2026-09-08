using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Llm.Anthropic.Tests;

public class AnthropicLlmModuleTests
{
    [Fact]
    public void Module_Id_IsLlmAnthropic()
    {
        var module = new AnthropicLlmModule();
        module.Id.Should().Be("llm.anthropic");
    }

    [Fact]
    public void ProvidedSeams_ContainsLlm()
    {
        var module = new AnthropicLlmModule();
        module.ProvidedSeams.Should().Equal("llm");
    }

    [Fact]
    public void ProvidedTools_IsEmpty()
    {
        var module = new AnthropicLlmModule();
        module.ProvidedTools.Should().BeEmpty();
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new AnthropicLlmModule();
        module.DependsOn.Should().BeEmpty();
    }
}

public class AnthropicLlmClientFactoryTests
{
    [Fact]
    public void SupportsType_IsCaseInsensitive()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);

        factory.SupportsType("anthropic").Should().BeTrue();
        factory.SupportsType("Anthropic").Should().BeTrue();
        factory.SupportsType("ANTHROPIC").Should().BeTrue();
        factory.SupportsType(ProviderTypes.Anthropic).Should().BeTrue();
        factory.SupportsType("openai").Should().BeFalse();
    }

    [Fact]
    public void SupportedTypes_ContainsOnlyAnthropic()
    {
        var factory = new AnthropicLlmClientFactory(NullLoggerFactory.Instance);
        factory.SupportedTypes.Should().Equal(ProviderTypes.Anthropic);
    }
}
