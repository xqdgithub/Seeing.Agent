using FluentAssertions;
using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests;

/// <summary>
/// Task 29: MemoryHookHandler 单元测试
/// 测试 MemoryHookPoints 的定义
/// </summary>
public class MemoryHookHandlerTests
{
    [Fact(DisplayName = "Hook 点应定义正确的常量")]
    public void HookPoints_ShouldHaveCorrectConstants()
    {
        // Assert
        MemoryHookPoints.Created.Should().Be("memory.created");
        MemoryHookPoints.Searched.Should().Be("memory.searched");
        MemoryHookPoints.Retrieved.Should().Be("memory.retrieved");
        MemoryHookPoints.Updated.Should().Be("memory.updated");
        MemoryHookPoints.Deleted.Should().Be("memory.deleted");
    }

    [Fact(DisplayName = "所有 Hook 点应以 memory. 开头")]
    public void HookPoints_ShouldStartWithMemoryPrefix()
    {
        // Assert
        MemoryHookPoints.Created.Should().StartWith("memory.");
        MemoryHookPoints.Searched.Should().StartWith("memory.");
        MemoryHookPoints.Retrieved.Should().StartWith("memory.");
        MemoryHookPoints.Updated.Should().StartWith("memory.");
        MemoryHookPoints.Deleted.Should().StartWith("memory.");
    }

    [Fact(DisplayName = "Hook 点名称应唯一")]
    public void HookPoints_ShouldBeUnique()
    {
        // Arrange
        var hookPoints = new[]
        {
            MemoryHookPoints.Created,
            MemoryHookPoints.Searched,
            MemoryHookPoints.Retrieved,
            MemoryHookPoints.Updated,
            MemoryHookPoints.Deleted
        };

        // Assert
        hookPoints.Should().OnlyHaveUniqueItems();
    }

    [Fact(DisplayName = "Hook 点总数应为 5")]
    public void HookPoints_CountShouldBe5()
    {
        // Arrange - 使用反射获取所有常量
        var constants = typeof(MemoryHookPoints)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => f.GetValue(null) as string)
            .ToList();

        // Assert
        constants.Should().HaveCount(5);
    }
}