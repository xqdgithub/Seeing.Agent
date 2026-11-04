using FluentAssertions;
using Seeing.Agent.Memory.Configuration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

public class MemoryOptionsTests
{
    [Fact]
    public void Enabled_DefaultValue_ShouldBeFalse()
    {
        // 双保险：默认 false 防「升级即自动开跑向量索引」（spec §7.8.1）。
        new MemoryEmbeddingOptions().Enabled.Should().BeFalse();
        new MemoryOptions().Embedding.Enabled.Should().BeFalse();
    }

    [Fact]
    public void IsEmbeddingConfigured_WhenDisabled_ShouldBeFalse()
    {
        var options = new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = false, ModelId = "openai/text-embedding-3-small" }
        };
        options.IsEmbeddingConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsEmbeddingConfigured_WhenEnabledButNoModelId_ShouldBeFalse()
    {
        var options = new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = null }
        };
        options.IsEmbeddingConfigured.Should().BeFalse();

        options.Embedding.ModelId = "   ";
        options.IsEmbeddingConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsEmbeddingConfigured_WhenEnabledAndModelIdSet_ShouldBeTrue()
    {
        var options = new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = "openai/text-embedding-3-small" }
        };
        options.IsEmbeddingConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsEmbeddingConfigured_ShouldNotConsultCatalog()
    {
        // 目录是异步填充的：DI 期查目录会产生启动竞态（spec §7.8 第 2 条）。
        // 结构证据——本类型是零构造参数的 POCO，没有任何协作者可注入，判定只能来自配置本身。
        typeof(MemoryOptions).GetConstructors().Should().ContainSingle()
            .Which.GetParameters().Should().BeEmpty("IsEmbeddingConfigured 必须是纯配置判定，不得依赖模型目录");
        typeof(MemoryOptions)
            .GetProperties()
            .Where(p => p.PropertyType.Name.StartsWith("IModel", StringComparison.Ordinal)
                        || p.PropertyType.Name.Contains("Manager", StringComparison.Ordinal))
            .Should().BeEmpty("MemoryOptions 不得持有模型目录协作者");

        // 行为证据——未配 Enabled 时，任何目录状态都改变不了结论。
        var options = new MemoryOptions();
        options.IsEmbeddingConfigured.Should().BeFalse();
        options.Embedding.ModelId = "openai/text-embedding-3-small";
        options.IsEmbeddingConfigured.Should().BeFalse();
        options.Embedding.Enabled = true;
        options.IsEmbeddingConfigured.Should().BeTrue();
    }

    [Fact]
    public void MemoryEmbeddingOptions_ShouldDropProviderModelDimensions()
    {
        // 旧的三字段旁路配置已删除：能力路由由 provider/model 引用与模型目录承担。
        var props = typeof(MemoryEmbeddingOptions).GetProperties().Select(p => p.Name);
        props.Should().BeEquivalentTo(new[] { nameof(MemoryEmbeddingOptions.Enabled), nameof(MemoryEmbeddingOptions.ModelId) });
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
