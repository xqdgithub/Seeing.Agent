using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Tools.BuiltIn.Plan;
using Xunit;

namespace Seeing.Agent.Tests.Plan;

public class PlanModelTests
{
    [Fact]
    public void PlanModel_Defaults()
    {
        // Arrange & Act
        var plan = new PlanModel();

        // Assert
        plan.Id.Should().NotBeEmpty();
        plan.Status.Should().Be(PlanStatus.Draft);
        plan.Tasks.Should().BeEmpty();
        plan.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PlanTask_Defaults()
    {
        // Arrange & Act
        var task = new PlanTask();

        // Assert
        task.Id.Should().NotBeEmpty();
        task.Status.Should().Be(PlanTaskStatus.Pending);
        task.Priority.Should().Be(0);
        task.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void PlanTask_WithDependencies_ShouldStore()
    {
        // Arrange & Act
        var task = new PlanTask
        {
            Title = "Test Task",
            Dependencies = new List<string> { "dep1", "dep2" }
        };

        // Assert
        task.Dependencies.Should().HaveCount(2);
    }
}

public class PlanManagerTests
{
    [Fact]
    public async Task CreatePlanAsync_ShouldCreatePlan()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var plan = await manager.CreatePlanAsync("Test Plan", "Test Description");

        // Assert
        plan.Name.Should().Be("Test Plan");
        plan.Description.Should().Be("Test Description");
        plan.Status.Should().Be(PlanStatus.Draft);
    }

    [Fact]
    public async Task AddTaskAsync_ShouldAddTask()
    {
        // Arrange
        var manager = CreateManager();
        var plan = await manager.CreatePlanAsync("Test", "Test");

        // Act
        var task = await manager.AddTaskAsync(plan.Id, "Task 1", "First task");

        // Assert
        task.Title.Should().Be("Task 1");
        task.Description.Should().Be("First task");
        task.Status.Should().Be(PlanTaskStatus.Pending);
    }

    [Fact]
    public async Task UpdateTaskStatusAsync_ShouldUpdateStatus()
    {
        // Arrange
        var manager = CreateManager();
        var plan = await manager.CreatePlanAsync("Test", "Test");
        var task = await manager.AddTaskAsync(plan.Id, "Task 1");

        // Act
        var result = await manager.UpdateTaskStatusAsync(plan.Id, task.Id, PlanTaskStatus.Completed);

        // Assert
        result.Should().BeTrue();
        var updatedPlan = await manager.GetPlanAsync(plan.Id);
        updatedPlan!.Tasks[0].Status.Should().Be(PlanTaskStatus.Completed);
    }

    [Fact]
    public async Task GetNextTaskAsync_ShouldReturnHighestPriorityPending()
    {
        // Arrange
        var manager = CreateManager();
        var plan = await manager.CreatePlanAsync("Test", "Test");
        await manager.AddTaskAsync(plan.Id, "Low Priority", priority: 1);
        await manager.AddTaskAsync(plan.Id, "High Priority", priority: 10);

        // Act
        var nextTask = await manager.GetNextTaskAsync(plan.Id);

        // Assert
        nextTask.Should().NotBeNull();
        nextTask!.Title.Should().Be("High Priority");
    }

    [Fact]
    public async Task GetNextTaskAsync_WithDependencies_ShouldRespectOrder()
    {
        // Arrange
        var manager = CreateManager();
        var plan = await manager.CreatePlanAsync("Test", "Test");
        var task1 = await manager.AddTaskAsync(plan.Id, "Task 1");
        await manager.AddTaskAsync(plan.Id, "Task 2", dependencies: new List<string> { task1.Id });

        // Act - Task 2 depends on Task 1, so Task 1 should be next
        var nextTask = await manager.GetNextTaskAsync(plan.Id);

        // Assert
        nextTask!.Title.Should().Be("Task 1");
    }

    [Fact]
    public async Task ListPlansAsync_ShouldReturnPlans()
    {
        // Arrange
        var manager = CreateManager();
        await manager.CreatePlanAsync("Plan 1", "Test");
        await manager.CreatePlanAsync("Plan 2", "Test");

        // Act
        var plans = await manager.ListPlansAsync();

        // Assert
        plans.Should().HaveCount(2);
    }

    private static PlanManager CreateManager()
    {
        var logger = new Mock<ILogger<PlanManager>>();
        var tempPath = Path.Combine(Path.GetTempPath(), $"plans-{Guid.NewGuid():N}");
        return new PlanManager(logger.Object, tempPath);
    }
}
