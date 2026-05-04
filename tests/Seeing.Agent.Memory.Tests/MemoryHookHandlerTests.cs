using FluentAssertions;
using Seeing.Agent.Core.Hooks;
using Xunit;

namespace Seeing.Agent.Memory.Tests;

public class MemoryHookHandlerTests
{
    [Fact(DisplayName = "HookRegistry Memory 点应定义正确的常量")]
    public void HookRegistry_MemoryPoints_ShouldHaveCorrectConstants()
    {
        HookRegistry.MemoryBeforeStore.Point.Should().Be("memory.before_store");
        HookRegistry.MemoryAfterStore.Point.Should().Be("memory.after_store");
        HookRegistry.MemoryBeforeRetrieve.Point.Should().Be("memory.before_retrieve");
        HookRegistry.MemoryAfterRetrieve.Point.Should().Be("memory.after_retrieve");
        HookRegistry.MemoryBeforeClear.Point.Should().Be("memory.before_clear");
        HookRegistry.MemoryAfterClear.Point.Should().Be("memory.after_clear");
    }

    [Fact(DisplayName = "所有 Memory Hook 点应以 memory. 开头")]
    public void HookRegistry_MemoryPoints_ShouldStartWithMemoryPrefix()
    {
        HookRegistry.MemoryBeforeStore.Point.Should().StartWith("memory.");
        HookRegistry.MemoryAfterStore.Point.Should().StartWith("memory.");
        HookRegistry.MemoryBeforeRetrieve.Point.Should().StartWith("memory.");
        HookRegistry.MemoryAfterRetrieve.Point.Should().StartWith("memory.");
        HookRegistry.MemoryBeforeClear.Point.Should().StartWith("memory.");
        HookRegistry.MemoryAfterClear.Point.Should().StartWith("memory.");
    }

    [Fact(DisplayName = "Memory Hook 点名称应唯一")]
    public void HookRegistry_MemoryPoints_ShouldBeUnique()
    {
        var hookPoints = new[]
        {
            HookRegistry.MemoryBeforeStore.Point,
            HookRegistry.MemoryAfterStore.Point,
            HookRegistry.MemoryBeforeRetrieve.Point,
            HookRegistry.MemoryAfterRetrieve.Point,
            HookRegistry.MemoryBeforeClear.Point,
            HookRegistry.MemoryAfterClear.Point
        };

        hookPoints.Should().OnlyHaveUniqueItems();
    }

    [Fact(DisplayName = "Memory Hook 点总数应为 6")]
    public void HookRegistry_MemoryPoints_CountShouldBe6()
    {
        var memoryPoints = new[]
        {
            HookRegistry.MemoryBeforeStore,
            HookRegistry.MemoryAfterStore,
            HookRegistry.MemoryBeforeRetrieve,
            HookRegistry.MemoryAfterRetrieve,
            HookRegistry.MemoryBeforeClear,
            HookRegistry.MemoryAfterClear
        };

        memoryPoints.Should().HaveCount(6);
    }

    [Fact(DisplayName = "Memory Hook 点应为 FireAndForget 策略")]
    public void HookRegistry_MemoryPoints_ShouldBeFireAndForgetPolicy()
    {
        HookRegistry.MemoryBeforeStore.Policy.Should().Be(HookPolicy.FireAndForget);
        HookRegistry.MemoryAfterStore.Policy.Should().Be(HookPolicy.FireAndForget);
        HookRegistry.MemoryBeforeRetrieve.Policy.Should().Be(HookPolicy.FireAndForget);
        HookRegistry.MemoryAfterRetrieve.Policy.Should().Be(HookPolicy.FireAndForget);
        HookRegistry.MemoryBeforeClear.Policy.Should().Be(HookPolicy.FireAndForget);
        HookRegistry.MemoryAfterClear.Policy.Should().Be(HookPolicy.FireAndForget);
    }
}