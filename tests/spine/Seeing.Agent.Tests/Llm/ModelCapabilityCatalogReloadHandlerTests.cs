using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Xunit;
using IModelConfigManager = Seeing.Agent.Llm.IModelConfigManager;

namespace Seeing.Agent.Tests.Llm;

public class ModelCapabilityCatalogReloadHandlerTests
{
    [Fact]
    public async Task InvalidateTrue_CallsRefreshCatalog()
    {
        var catalog = new Mock<IModelConfigManager>(MockBehavior.Strict);
        catalog
            .Setup(m => m.RefreshCatalogAsync(null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new ModelCapabilityCatalogReloadHandler(catalog.Object);
        await handler.ReloadAsync(new ModelCapabilitiesChange
        {
            Reason = ModelCapabilitiesChangeReason.SourceDataChanged,
            InvalidateModelCatalog = true
        });

        catalog.Verify(
            m => m.RefreshCatalogAsync(null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvalidateFalse_SkipsRefreshCatalog()
    {
        var catalog = new Mock<IModelConfigManager>(MockBehavior.Strict);
        var handler = new ModelCapabilityCatalogReloadHandler(catalog.Object);

        await handler.ReloadAsync(new ModelCapabilitiesChange
        {
            Reason = ModelCapabilitiesChangeReason.SourceDataChanged,
            InvalidateModelCatalog = false
        });

        catalog.Verify(
            m => m.RefreshCatalogAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void ComponentId_IsModelCapabilityCatalog()
    {
        var handler = new ModelCapabilityCatalogReloadHandler(Mock.Of<IModelConfigManager>());
        handler.ComponentId.Should().Be("model-capability-catalog");
    }
}
