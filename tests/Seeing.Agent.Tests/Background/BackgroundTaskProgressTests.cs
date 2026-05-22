using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Interfaces;
using Xunit;

namespace Seeing.Agent.Tests.Background;

public class BackgroundTaskProgressTests
{
    [Fact]
    public void BackgroundTaskProgress_Defaults()
    {
        // Arrange & Act
        var progress = new BackgroundTaskProgress();

        // Assert
        progress.TaskId.Should().BeEmpty();
        progress.Percent.Should().Be(0);
        progress.Type.Should().Be(ProgressType.Update);
        progress.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }
}

public class BackgroundTaskInfoTests
{
    [Fact]
    public void BackgroundTaskInfo_Defaults()
    {
        // Arrange & Act
        var info = new BackgroundTaskInfo();

        // Assert
        info.Id.Should().BeEmpty();
        info.Status.Should().Be(BackgroundTaskStatus.Pending);
        info.Progress.Should().Be(0);
        info.OutputLines.Should().BeEmpty();
    }

    [Fact]
    public void BackgroundTaskInfo_WithProgress_ShouldSetFields()
    {
        // Arrange & Act
        var info = new BackgroundTaskInfo
        {
            Progress = 50,
            ProgressMessage = "Processing...",
            ProgressUpdatedAt = DateTimeOffset.UtcNow
        };

        // Assert
        info.Progress.Should().Be(50);
        info.ProgressMessage.Should().Be("Processing...");
        info.ProgressUpdatedAt.Should().NotBeNull();
    }
}

public class BackgroundTaskManagerProgressTests
{
    [Fact]
    public void SubscribeProgress_ShouldReturnObservable()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var observable = manager.SubscribeProgress("test-task-id");

        // Assert
        observable.Should().NotBeNull();
    }

    [Fact]
    public void SubscribeOutput_ShouldReturnObservable()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var observable = manager.SubscribeOutput("test-task-id");

        // Assert
        observable.Should().NotBeNull();
    }

    [Fact]
    public async Task InjectResultAsync_NonExistentTask_ShouldReturnFalse()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var result = await manager.InjectResultAsync("non-existent", "session-id");

        // Assert
        result.Should().BeFalse();
    }

    private static BackgroundTaskManager CreateManager()
    {
        var agentRegistry = new Mock<IAgentRegistry>();
        var logger = new Mock<ILogger<BackgroundTaskManager>>();
        return new BackgroundTaskManager(agentRegistry.Object, logger.Object, null);
    }
}
