using FluentAssertions;
using Seeing.Agent.Memory.Configuration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

public class MemoryOptionsTests
{
    [Fact]
    public void IsEmbeddingConfigured_WhenProviderOrModelEmpty_ShouldBeFalse()
    {
        var options = new MemoryOptions();
        options.IsEmbeddingConfigured.Should().BeFalse();

        options.Embedding.Provider = "openai";
        options.IsEmbeddingConfigured.Should().BeFalse();

        options.Embedding.Model = "text-embedding-3-small";
        options.IsEmbeddingConfigured.Should().BeTrue();
    }

    [Fact]
    public void MemoryOptions_ShouldNotExposeModuleEnabledSwitch()
    {
        // 模块启停的唯一真相源是模块生命周期（IModuleCatalog / IModuleHostedService），
        // MemoryOptions 不得再暴露第二套 Enabled 总开关。
        typeof(MemoryOptions).GetProperty("Enabled").Should().BeNull();
    }

    [Fact]
    public void Defaults_ShouldMatchSpec()
    {
        var o = new MemoryOptions();
        o.Capture.AutoCapture.Should().BeTrue();
        o.Capture.MaxSnippetChars.Should().Be(4096);
        o.Capture.QueueCapacity.Should().Be(256);
        o.Extraction.MinImportance.Should().Be(0.5);
        o.Evolution.IdleMinutes.Should().Be(15);
        o.Retrieval.Mode.Should().Be(MemoryRetrievalMode.Both);
        o.Retrieval.InjectTimeoutMs.Should().Be(150);
    }
}
