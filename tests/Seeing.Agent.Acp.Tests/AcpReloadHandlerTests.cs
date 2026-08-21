using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Hosting;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpReloadHandlerTests
{
    [Fact]
    public async Task ReloadAsync_包含Acp配置节_应调用重载器()
    {
        // Arrange
        var reloader = new Mock<IAcpConfigurationReloader>();
        var handler = new AcpReloadHandler(reloader.Object);

        // Act
        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Acp" } });

        // Assert
        reloader.Verify(r => r.ReloadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReloadAsync_空配置节表示全量_应调用重载器()
    {
        // Arrange
        var reloader = new Mock<IAcpConfigurationReloader>();
        var handler = new AcpReloadHandler(reloader.Object);

        // Act
        await handler.ReloadAsync(new ConfigChange());

        // Assert
        reloader.Verify(r => r.ReloadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReloadAsync_非Acp配置节_不应调用重载器()
    {
        // Arrange
        var reloader = new Mock<IAcpConfigurationReloader>();
        var handler = new AcpReloadHandler(reloader.Object);

        // Act
        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Mcp" } });

        // Assert
        reloader.Verify(r => r.ReloadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReloadAsync_非ConfigChange信号_不应调用重载器()
    {
        // Arrange
        var reloader = new Mock<IAcpConfigurationReloader>();
        var handler = new AcpReloadHandler(reloader.Object);

        // Act
        await handler.ReloadAsync(new WorkspaceChange { OldWorkspace = "/a", NewWorkspace = "/b" });

        // Assert
        reloader.Verify(r => r.ReloadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ComponentId_应返回acp()
    {
        // Arrange
        var handler = new AcpReloadHandler(Mock.Of<IAcpConfigurationReloader>());

        // Act & Assert
        handler.ComponentId.Should().Be("acp");
        handler.ChangeTypes.Should().Contain(typeof(ConfigChange));
    }
}
