using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Embedding;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

public class EmbeddingStatusTests
{
    [Fact]
    public void Enabled_DefaultFalse_ShouldBeUnavailable()
    {
        var models = CreateCatalog(hasModel: true);
        var status = new ConfigurableEmbeddingStatus(
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                // 只配 ModelId、不显式打开 Enabled：默认 false 即不启用。
                Embedding = new MemoryEmbeddingOptions { ModelId = "openai/text-embedding-3-small" }
            }),
            models.Object);

        status.IsAvailable.Should().BeFalse();
        status.Reason.Should().Contain("Enabled");
    }

    [Fact]
    public void EnabledTrueButNoModelId_ShouldRemainUnavailable()
    {
        var models = CreateCatalog(hasModel: true);
        var status = new ConfigurableEmbeddingStatus(
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Embedding = new MemoryEmbeddingOptions { Enabled = true }
            }),
            models.Object);

        status.IsAvailable.Should().BeFalse();
        status.Reason.Should().Contain("ModelId");
    }

    [Fact]
    public void DefaultModelsEmbeddingSetButMemoryModelIdEmpty_ShouldStillBeUnavailable()
    {
        // 决议 10：Memory 不读全局 DefaultModels.embedding——即使它配好了也不启用。
        var models = CreateCatalog(hasModel: true, defaultEmbeddingModel: "openai/text-embedding-3-small");
        var status = new ConfigurableEmbeddingStatus(
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = null }
            }),
            models.Object);

        status.IsAvailable.Should().BeFalse();
        status.Reason.Should().Contain("ModelId");

        models.Verify(m => m.GetDefaultModel(It.IsAny<LlmCapabilityKind>()), Times.Never,
            "Memory 绝不查询默认模型：向量索引有后台成本，只能由 memory.json 显式开启");
        models.Verify(m => m.GetModelsByType(It.IsAny<ModelType>(), It.IsAny<string?>()), Times.Never,
            "绝不从目录挑「第一个 Embedding 模型」");
        models.Verify(m => m.GetModels(), Times.Never, "绝不枚举目录做启发式选择");
    }

    [Fact]
    public void ModelMissingFromCatalog_ShouldBeUnavailable()
    {
        var models = CreateCatalog(hasModel: false);
        var status = new ConfigurableEmbeddingStatus(
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Embedding = new MemoryEmbeddingOptions
                {
                    Enabled = true,
                    ModelId = "openai/text-embedding-3-small"
                }
            }),
            models.Object);

        status.IsAvailable.Should().BeFalse();
        status.Reason.Should().Contain("openai/text-embedding-3-small");
    }

    [Fact]
    public void EnabledWithModelIdPresentInCatalog_ShouldBeAvailable()
    {
        var models = CreateCatalog(hasModel: true);
        var status = new ConfigurableEmbeddingStatus(
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Embedding = new MemoryEmbeddingOptions
                {
                    Enabled = true,
                    ModelId = "openai/text-embedding-3-small"
                }
            }),
            models.Object);

        status.IsAvailable.Should().BeTrue();
        status.Reason.Should().BeNull();
    }

    private static Mock<IModelManager> CreateCatalog(
        bool hasModel,
        string? defaultEmbeddingModel = null)
    {
        var models = new Mock<IModelManager>();
        models.Setup(m => m.GetModel("openai/text-embedding-3-small"))
            .Returns(hasModel ? new ModelConfig { Id = "text-embedding-3-small" } : null);
        // 即便全局默认模型配了 embedding，Memory 也不得读它（决议 10）。
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Embedding))
            .Returns(defaultEmbeddingModel);
        return models;
    }
}
