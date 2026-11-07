using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Extensions;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Module;

/// <summary>模块 id、启停门控与 DI 幂等。</summary>
public sealed class ReflectionModuleTests
{
    [Fact]
    public void Module_Id_Should_Be_Reflection_And_Provide_No_Tool()
    {
        var module = new ReflectionModule();

        module.Id.Should().Be("reflection");

        // 复盘 agent 纯只读：技能写入在宿主侧执行器内完成，不经工具。
        module.ProvidedTools.Should().BeEmpty();
    }

    [Fact]
    public async Task Activate_Should_Set_Activity_Active()
    {
        var activity = new ReflectionModuleActivity { IsActive = false };
        var module = new ReflectionModule(activity);
        using var provider = new ServiceCollection().BuildServiceProvider();

        await module.ActivateAsync(provider);

        activity.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Deactivate_Should_Clear()
    {
        var activity = new ReflectionModuleActivity { IsActive = true };
        var module = new ReflectionModule(activity);
        using var provider = new ServiceCollection().BuildServiceProvider();

        await module.DeactivateAsync(provider);

        activity.IsActive.Should().BeFalse();
    }

    [Fact]
    public void AddSeeingReflection_Should_Be_Idempotent()
    {
        var services = new ServiceCollection();
        services.AddSeeingReflection();
        services.AddSeeingReflection();

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IReflectionQueue>();
        var second = provider.GetRequiredService<IReflectionQueue>();

        first.Should().BeSameAs(second);
    }
}
