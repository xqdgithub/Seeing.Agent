using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

public class OpenAiLlmModuleTests
{
    [Fact]
    public void Module_Id_IsLlmOpenAi()
    {
        var module = new OpenAiLlmModule();
        module.Id.Should().Be("llm.openai");
    }

    [Fact]
    public void ProvidedSeams_ContainsLlm()
    {
        var module = new OpenAiLlmModule();
        module.ProvidedSeams.Should().Equal("llm");
    }

    [Fact]
    public void ProvidedTools_IsEmpty()
    {
        var module = new OpenAiLlmModule();
        module.ProvidedTools.Should().BeEmpty();
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new OpenAiLlmModule();
        module.DependsOn.Should().BeEmpty();
    }
}

public class OpenAiLlmClientFactoryTests
{
    [Fact]
    public void SupportsType_IsCaseInsensitive()
    {
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);

        factory.SupportsType("openai").Should().BeTrue();
        factory.SupportsType("OpenAI").Should().BeTrue();
        factory.SupportsType("OPENAI").Should().BeTrue();
        factory.SupportsType(ProviderTypes.OpenAi).Should().BeTrue();
        factory.SupportsType("anthropic").Should().BeFalse();
    }

    [Fact]
    public void SupportedTypes_ContainsOnlyOpenAi()
    {
        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);
        factory.SupportedTypes.Should().Equal(ProviderTypes.OpenAi);
    }
}
