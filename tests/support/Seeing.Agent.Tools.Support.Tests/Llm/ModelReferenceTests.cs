using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools.Support.Llm;
using Xunit;

namespace Seeing.Agent.Tools.Support.Tests.Llm;

public class ModelReferenceTests
{
    [Fact]
    public void ToWireModelId_Should_Prefer_Catalog_Id_Over_Reference()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel("openai/whisper-1"))
              .Returns(new ModelConfig { Id = "whisper-1-2024" });

        ModelReference.ToWireModelId(models.Object, "openai/whisper-1")
            .Should().Be("whisper-1-2024");
    }

    [Fact]
    public void ToWireModelId_Should_Strip_Provider_Prefix_When_Not_In_Catalog()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)null);

        ModelReference.ToWireModelId(models.Object, "siliconflow/whisper-large-v3")
            .Should().Be("whisper-large-v3");
    }

    [Fact]
    public void ToWireModelId_Should_Return_Input_Unchanged_When_No_Slash()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel(It.IsAny<string>())).Returns((ModelConfig?)null);

        ModelReference.ToWireModelId(models.Object, "whisper-1").Should().Be("whisper-1");
    }

    [Fact]
    public void ToWireModelId_Should_Ignore_Blank_Catalog_Id()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel("openai/whisper-1"))
              .Returns(new ModelConfig { Id = "  " });

        ModelReference.ToWireModelId(models.Object, "openai/whisper-1")
            .Should().Be("whisper-1");
    }
}
