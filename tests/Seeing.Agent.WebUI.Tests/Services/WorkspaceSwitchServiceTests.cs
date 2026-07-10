using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// WorkspaceSwitchService 单元测试
/// </summary>
public class WorkspaceSwitchServiceTests : IDisposable
{
    private readonly Mock<IWorkspaceProvider> _workspaceMock;
    private readonly Mock<SeeingConfigService> _configServiceMock;
    private readonly Mock<SkillStateService> _skillStateMock;
    private readonly Mock<ToolStateService> _toolStateMock;
    private readonly Mock<ILogger<WorkspaceSwitchService>> _loggerMock;
    private readonly string _testDirectory;

    public WorkspaceSwitchServiceTests()
    {
        _workspaceMock = new Mock<IWorkspaceProvider>();
        _configServiceMock = new Mock<SeeingConfigService>(MockBehavior.Loose);
        _skillStateMock = new Mock<SkillStateService>(MockBehavior.Loose);
        _toolStateMock = new Mock<ToolStateService>(MockBehavior.Loose);
        _loggerMock = new Mock<ILogger<WorkspaceSwitchService>>();
        
        _testDirectory = Path.Combine(Path.GetTempPath(), $"workspace_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private WorkspaceSwitchService CreateService()
    {
        _workspaceMock.Setup(x => x.WorkspaceRoot).Returns("/old");
        
        return new WorkspaceSwitchService(
            _workspaceMock.Object,
            _configServiceMock.Object,
            _skillStateMock.Object,
            _toolStateMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task SwitchWorkspaceAsync_ValidPath_ShouldUpdateWorkspace()
    {
        // Arrange
        var service = CreateService();
        var newPath = _testDirectory;

        // Act
        var result = await service.SwitchWorkspaceAsync(newPath);

        // Assert
        result.Should().BeTrue();
        _workspaceMock.Verify(x => x.SetWorkspaceRoot(newPath), Times.Once);
        _configServiceMock.Verify(x => x.ReloadAsync(It.IsAny<CancellationToken>()), Times.Once);
        _skillStateMock.Verify(x => x.LoadAsync(It.IsAny<CancellationToken>()), Times.Once);
        _toolStateMock.Verify(x => x.LoadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwitchWorkspaceAsync_EmptyPath_ShouldReturnFalse()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = await service.SwitchWorkspaceAsync("");

        // Assert
        result.Should().BeFalse();
        _workspaceMock.Verify(x => x.SetWorkspaceRoot(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SwitchWorkspaceAsync_NonExistentPath_ShouldReturnFalse()
    {
        // Arrange
        var service = CreateService();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}");

        // Act
        var result = await service.SwitchWorkspaceAsync(nonExistentPath);

        // Assert
        result.Should().BeFalse();
        _workspaceMock.Verify(x => x.SetWorkspaceRoot(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SwitchWorkspaceAsync_SamePath_ShouldReturnTrueWithoutReload()
    {
        // Arrange
        var newPath = _testDirectory;
        _workspaceMock.Setup(x => x.WorkspaceRoot).Returns(newPath);
        var service = CreateService();

        // Act
        var result = await service.SwitchWorkspaceAsync(newPath);

        // Assert
        result.Should().BeTrue();
        _workspaceMock.Verify(x => x.SetWorkspaceRoot(It.IsAny<string>()), Times.Never);
        _configServiceMock.Verify(x => x.ReloadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SwitchWorkspaceAsync_ConfigServiceThrows_ShouldRollbackAndReturnFalse()
    {
        // Arrange
        var service = CreateService();
        var newPath = _testDirectory;
        
        _configServiceMock
            .Setup(x => x.ReloadAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Config error"));

        // Act
        var result = await service.SwitchWorkspaceAsync(newPath);

        // Assert
        result.Should().BeFalse();
        // 验证回滚
        _workspaceMock.Verify(x => x.SetWorkspaceRoot("/old"), Times.Once);
    }

    [Fact]
    public void ValidateWorkspacePath_EmptyPath_ShouldReturnFalse()
    {
        // Arrange
        var service = CreateService();

        // Act
        var (valid, error) = service.ValidateWorkspacePath("");

        // Assert
        valid.Should().BeFalse();
        error.Should().Be("路径不能为空");
    }

    [Fact]
    public void ValidateWorkspacePath_NonExistentPath_ShouldReturnFalse()
    {
        // Arrange
        var service = CreateService();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}");

        // Act
        var (valid, error) = service.ValidateWorkspacePath(nonExistentPath);

        // Assert
        valid.Should().BeFalse();
        error.Should().Be("目录不存在");
    }

    [Fact]
    public void ValidateWorkspacePath_ValidPath_ShouldReturnTrue()
    {
        // Arrange
        var service = CreateService();

        // Act
        var (valid, error) = service.ValidateWorkspacePath(_testDirectory);

        // Assert
        valid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void CurrentWorkspace_ShouldReturnWorkspaceRoot()
    {
        // Arrange
        _workspaceMock.Setup(x => x.WorkspaceRoot).Returns("/test/workspace");
        var service = CreateService();

        // Act
        var current = service.CurrentWorkspace;

        // Assert
        current.Should().Be("/test/workspace");
    }
}
